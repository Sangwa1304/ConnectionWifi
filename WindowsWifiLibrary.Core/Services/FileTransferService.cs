using LibraryMI.Interfaces;
using LibraryMI.Models;
using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsWifiLibrary.Core.Models;

namespace WindowsWifiLibrary.Core.Services;

public class FileTransferService : IFileTransferService
{
    private const int MaxRequestBodyBytes = 64 * 1024;
    private const int MaxFilesPerSession = 10_000;
    private const int MaxDirectoryDepth = 32;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    public event EventHandler<TransferSessionMetadata>? IncomingTransferRequested;

    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerLoopTask;
    private readonly HttpClient _httpClient = new();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<TransferDecision>> _pairingRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _servedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TransferSessionMetadata> _activeSessions = new(StringComparer.OrdinalIgnoreCase);

    public Task StartListeningAsync(string localIp, int port, CancellationToken cancellationToken)
    {
        if (_listener?.IsListening is true) return Task.CompletedTask;

        _listener = new HttpListener();
        string host = localIp;
        if (IPAddress.TryParse(localIp, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            host = $"[{localIp}]";
        }

        _listener.Prefixes.Add($"http://{host}:{port}/");
        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _listenerLoopTask = ListenAsync(_listener, _listenerCts.Token);
        return Task.CompletedTask;
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = HandleIncomingRequestAsync(context);
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    public Task<TransferValidationResult> ValidateTransferAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new TransferValidationResult();
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? rawPath in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            string? trimmedPath = rawPath.Trim();
            if (!File.Exists(trimmedPath) && !Directory.Exists(trimmedPath))
            {
                result.Errors.Add($"Le chemin '{trimmedPath}' est introuvable ou invalide.");
                continue;
            }

            string? canonicalPath = Path.GetFullPath(trimmedPath);
            if (!normalizedPaths.Add(canonicalPath))
            {
                result.Warnings.Add($"Le chemin '{canonicalPath}' est dupliqué dans le transfert.");
                continue;
            }

            result.NormalizedPaths.Add(canonicalPath);
        }

        if (result.NormalizedPaths.Count == 0)
        {
            result.IsValid = false;
            result.Errors.Add("Aucun fichier ou dossier valide n'a été fourni.");
        }

        result.IsValid = result.IsValid && result.Errors.Count == 0;
        return Task.FromResult(result);
    }

    public async Task<TransferDecision> AcceptTransferAsync(TransferSessionMetadata session, string? destinationFolder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsValidSession(session))
        {
            throw new InvalidOperationException("La session de transfert est invalide ou expirée.");
        }

        var destination = string.IsNullOrWhiteSpace(destinationFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Wi-Fi P2P")
            : Path.GetFullPath(destinationFolder);
        Directory.CreateDirectory(destination);

        var decision = new TransferDecision
        {
            Accepted = true,
            DestinationFolder = destination,
            SessionToken = session.SessionToken,
            Reason = "Transfer accepted"
        };

        session.NegotiationStatus = TransferNegotiationStatus.Accepted;
        session.SessionState = TransferSessionState.ReadyToSend;
        session.TargetFolder = decision.DestinationFolder;

        try
        {
            string url = $"http://{session.SenderIp}:{session.SenderPort}/api/transfer/accept?sessionId={Uri.EscapeDataString(session.SessionId)}";
            await _httpClient.PostAsJsonAsync(url, decision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            session.SessionState = TransferSessionState.Failed;
            session.NegotiationStatus = TransferNegotiationStatus.Error;
            session.ErrorMessage = $"Impossible de notifier l'expéditeur : {ex.Message}";
            decision.Accepted = false;
        }

        return decision;
    }

    public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(sessionId) && _activeSessions.TryGetValue(sessionId, out var session))
        {
            session.SessionState = TransferSessionState.Cancelled;
            session.NegotiationStatus = TransferNegotiationStatus.Cancelled;
            session.ErrorMessage ??= "Le transfert a été annulé.";
            _activeSessions.TryRemove(sessionId, out _);
        }

        _servedFiles.Clear();
        return Task.CompletedTask;
    }

    public Task StopListeningAsync()
    {
        _listenerCts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _listenerLoopTask = null;
        _servedFiles.Clear();
        _activeSessions.Clear();
        return Task.CompletedTask;
    }

    private async Task HandleIncomingRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            string cleanPath = request.Url?.AbsolutePath ?? string.Empty;

            if (request.HttpMethod == "POST" && cleanPath == "/api/transfer/request")
            {
                var payload = await ReadRequestBodyAsync(request).ConfigureAwait(false);
                var metadata = JsonSerializer.Deserialize<TransferSessionMetadata>(payload);
                if (metadata != null && IsValidSession(metadata))
                {
                    _activeSessions[metadata.SessionId] = metadata;
                    metadata.SessionState = TransferSessionState.Negotiating;
                    metadata.NegotiationStatus = TransferNegotiationStatus.Pending;
                    IncomingTransferRequested?.Invoke(this, metadata);
                    response.StatusCode = (int)HttpStatusCode.Accepted;
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                }
            }
            else if (request.HttpMethod == "POST" && cleanPath == "/api/transfer/accept")
            {
                var payload = await ReadRequestBodyAsync(request).ConfigureAwait(false);
                var decision = JsonSerializer.Deserialize<TransferDecision>(payload);
                string sessionId = request.QueryString["sessionId"] ?? string.Empty;

                if (decision != null && !string.IsNullOrWhiteSpace(sessionId)
                    && _activeSessions.TryGetValue(sessionId, out var activeSession)
                    && CryptographicOperations.FixedTimeEquals(
                        Convert.FromBase64String(activeSession.SessionToken),
                        Convert.FromBase64String(decision.SessionToken ?? string.Empty)))
                {
                    if (_pairingRequests.TryGetValue(sessionId, out var tcs))
                    {
                        tcs.TrySetResult(decision);
                        _pairingRequests.TryRemove(sessionId, out _);
                    }
                    response.StatusCode = (int)HttpStatusCode.OK;
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                }
            }
            // CORRECTION: Ajout du endpoint manquant appelé par GetSessionAsync
            else if (request.HttpMethod == "GET" && cleanPath.StartsWith("/api/transfer/session/"))
            {
                string sessionId = Uri.UnescapeDataString(cleanPath.Substring("/api/transfer/session/".Length));
                if (!string.IsNullOrEmpty(sessionId) && _activeSessions.TryGetValue(sessionId, out var session)
                    && HasValidToken(request, session.SessionToken))
                {
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "application/json";
                    var json = JsonSerializer.Serialize(session);
                    var buffer = System.Text.Encoding.UTF8.GetBytes(json);
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
            else if (request.HttpMethod == "GET" && cleanPath == "/api/transfer/download")
            {
                string sessionId = request.QueryString["sessionId"] ?? string.Empty;
                string relativePath = request.QueryString["path"] ?? string.Empty;
                if (_activeSessions.TryGetValue(sessionId, out var session)
                    && HasValidToken(request, session.SessionToken)
                    && IsSafeRelativePath(relativePath)
                    && _servedFiles.TryGetValue(GetServedFileKey(sessionId, relativePath), out string? fullLocalPath)
                    && File.Exists(fullLocalPath))
                {
                    await ServeFileChunkAsync(request, response, fullLocalPath).ConfigureAwait(false);
                    return; 
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }
        catch (JsonException)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
        }
        catch (InvalidDataException)
        {
            response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
        }
        catch
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    private static async Task ServeFileChunkAsync(HttpListenerRequest request, HttpListenerResponse response, string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        long startByte = 0, endByte = fileInfo.Length - 1;
        var rangeHeader = request.Headers["Range"];

        if (!string.IsNullOrEmpty(rangeHeader))
        {
            var parts = rangeHeader.Replace("bytes=", "", StringComparison.OrdinalIgnoreCase).Split('-');
            long pEnd = fileInfo.Length - 1;
            if (parts.Length != 2 || !long.TryParse(parts[0], out var pStart) || pStart < 0
                || (parts[1].Length > 0 && (!long.TryParse(parts[1], out pEnd) || pEnd < pStart)))
            {
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                return;
            }
            startByte = pStart;
            if (parts[1].Length > 0) endByte = Math.Min(pEnd, fileInfo.Length - 1);
            if (startByte >= fileInfo.Length)
            {
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                return;
            }
            response.StatusCode = (int)HttpStatusCode.PartialContent;
            response.AddHeader("Content-Range", $"bytes {startByte}-{endByte}/{fileInfo.Length}");
        }
        else
        {
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        long contentLength = (endByte - startByte) + 1;
        response.ContentLength64 = contentLength;
        response.ContentType = "application/octet-stream";

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        fs.Seek(startByte, SeekOrigin.Begin);

        var buffer = new byte[81920];
        long totalSent = 0;
        while (totalSent < contentLength)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, contentLength - totalSent);
            int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, bytesToRead)).ConfigureAwait(false);
            if (bytesRead == 0) break;
            
            await response.OutputStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
            totalSent += bytesRead;
        }
    }

    public async Task<TransferSessionMetadata> PrepareMetadataAsync(IEnumerable<string> paths, string localIp, int port)
    {
        var session = new TransferSessionMetadata
        {
            SenderIp = localIp,
            SenderPort = port,
            SessionToken = CreateSessionToken(),
            SessionState = TransferSessionState.Created
        };
        _servedFiles.Clear();

        var validation = await ValidateTransferAsync(paths, CancellationToken.None).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            session.ErrorMessage = string.Join("; ", validation.Errors);
            session.SessionState = TransferSessionState.Failed;
            session.NegotiationStatus = TransferNegotiationStatus.Error;
            return session;
        }

        foreach (var normalizedPath in validation.NormalizedPaths)
        {
            if (File.Exists(normalizedPath))
            {
                var fileInfo = new FileInfo(normalizedPath);
                var item = new TransferItemMetadata
                {
                    RelativePath = fileInfo.Name,
                    FullLocalPath = fileInfo.FullName,
                    IsDirectory = false,
                    Size = fileInfo.Length
                };

                session.Items.Add(item);
                session.TotalSize += fileInfo.Length;
                session.TotalFiles++;
                _servedFiles[GetServedFileKey(session.SessionId, item.RelativePath)] = fileInfo.FullName;
            }
            else if (Directory.Exists(normalizedPath))
            {
                var directoryInfo = new DirectoryInfo(normalizedPath);
                session.Items.Add(BuildDirectoryMetadata(directoryInfo, directoryInfo.Name, session, 0));
            }
        }

        _activeSessions[session.SessionId] = session;
        return session;
    }

    private TransferItemMetadata BuildDirectoryMetadata(DirectoryInfo dir, string relativeBase, TransferSessionMetadata session, int depth)
    {
        if (depth > MaxDirectoryDepth) throw new InvalidOperationException("Le dossier est trop profond.");
        var dirMeta = new TransferItemMetadata { RelativePath = relativeBase, FullLocalPath = dir.FullName, IsDirectory = true };

        foreach (var file in dir.GetFiles())
        {
            string relPath = $"{relativeBase}/{file.Name}";
            var item = new TransferItemMetadata { RelativePath = relPath, FullLocalPath = file.FullName, IsDirectory = false, Size = file.Length };
            dirMeta.Children.Add(item);
            session.TotalSize += file.Length;
            session.TotalFiles++;
            if (session.TotalFiles >= MaxFilesPerSession) throw new InvalidOperationException("Le transfert contient trop de fichiers.");
            _servedFiles[GetServedFileKey(session.SessionId, relPath)] = file.FullName;
        }

        foreach (var subDir in dir.GetDirectories())
        {
            dirMeta.Children.Add(BuildDirectoryMetadata(subDir, $"{relativeBase}/{subDir.Name}", session, depth + 1));
        }

        return dirMeta;
    }

    public async Task<bool> RequestPairingAsync(PeerDevice targetDevice, TransferSessionMetadata metadata, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<TransferDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pairingRequests[metadata.SessionId] = tcs;

        try
        {
            metadata.SessionState = TransferSessionState.Negotiating;
            var response = await _httpClient.PostAsJsonAsync($"http://{targetDevice.IpAddress}:{targetDevice.Port}/api/transfer/request", metadata, cancellationToken).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                _pairingRequests.TryRemove(metadata.SessionId, out _);
                metadata.SessionState = TransferSessionState.Failed;
                return false;
            }

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                TransferDecision decision = await tcs.Task.ConfigureAwait(false);
                if (decision.Accepted)
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromBase64String(metadata.SessionToken),
                        Convert.FromBase64String(decision.SessionToken ?? string.Empty)))
                    {
                        metadata.SessionState = TransferSessionState.Failed;
                        metadata.ErrorMessage = "Jeton de session invalide.";
                        return false;
                    }
                    metadata.SessionState = TransferSessionState.ReadyToSend;
                    metadata.NegotiationStatus = TransferNegotiationStatus.Accepted;
                    metadata.TargetFolder = decision.DestinationFolder;
                    return true;
                }
                else
                {
                    metadata.SessionState = TransferSessionState.Cancelled;
                    metadata.NegotiationStatus = TransferNegotiationStatus.Refused;
                    metadata.ErrorMessage = decision.Reason ?? "Le transfert a été refusé par le destinataire.";
                    return false;
                }
            }
        }
        // CORRECTION: Interception propre du SocketException (Connexion refusée)
        catch (HttpRequestException ex)
        {
            metadata.SessionState = TransferSessionState.Failed;
            metadata.ErrorMessage = $"L'hôte distant ({targetDevice.IpAddress}:{targetDevice.Port}) a refusé la connexion. Vérifiez le pare-feu. Détail : {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            metadata.SessionState = TransferSessionState.Failed;
            metadata.ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            _pairingRequests.TryRemove(metadata.SessionId, out _);
        }
    }

    public async Task DownloadSessionAsync(TransferSessionMetadata session, string destinationFolder, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(session.SenderIp) || session.SenderPort <= 0)
        {
            throw new InvalidOperationException("Le transfert ne dispose pas d'une destination valide.");
        }

        long globalBytesTransferred = 0;
        string baseUrl = $"http://{session.SenderIp}:{session.SenderPort}/api/transfer/download";

        async Task ProcessItemAsync(TransferItemMetadata item)
        {
            string sanitizedRelativePath = item.RelativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (!IsSafeRelativePath(item.RelativePath))
            {
                throw new InvalidDataException($"Chemin distant invalide : {item.RelativePath}");
            }
            string targetPath = Path.Combine(destinationFolder, sanitizedRelativePath);
            var root = Path.GetFullPath(destinationFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(targetPath).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Le fichier distant sort du dossier de destination.");
            }

            if (item.IsDirectory)
            {
                Directory.CreateDirectory(targetPath);
                foreach (var child in item.Children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessItemAsync(child).ConfigureAwait(false);
                }
                return;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var fileProgress = new Progress<long>(bytesRead =>
            {
                Interlocked.Add(ref globalBytesTransferred, bytesRead);
                progress.Report(new FileTransferProgress
                {
                    BytesTransferred = Interlocked.Read(ref globalBytesTransferred),
                    TotalBytes = session.TotalSize,
                    CurrentFileName = item.RelativePath
                });
            });

            await DownloadFileAsync(
                $"{baseUrl}?sessionId={Uri.EscapeDataString(session.SessionId)}&path={Uri.EscapeDataString(item.RelativePath)}",
                session.SessionToken,
                targetPath,
                item.Size,
                cancellationToken,
                fileProgress).ConfigureAwait(false);
        }

        foreach (var item in session.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessItemAsync(item).ConfigureAwait(false);
        }
    }

    private async Task DownloadFileAsync(string url, string sessionToken, string destinationPath, long fileSize, CancellationToken token, IProgress<long> onProgress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Transfer-Token", sessionToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.part";
        try
        {
            await using var destinationStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using var sourceStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

        var buffer = new byte[81920];
        int bytesRead;
        long downloaded = 0;

        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
        {
            token.ThrowIfCancellationRequested();
            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), token).ConfigureAwait(false);
            downloaded += bytesRead;
            onProgress.Report(bytesRead);
        }

            if (downloaded != fileSize)
            {
                throw new InvalidOperationException($"Le fichier '{destinationPath}' n'a pas été entièrement téléchargé.");
            }
            File.Move(temporaryPath, destinationPath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _listenerCts?.Dispose();
    }

    private static string CreateSessionToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static bool IsValidSession(TransferSessionMetadata session) =>
        !string.IsNullOrWhiteSpace(session.SessionId)
        && Convert.TryFromBase64String(session.SessionToken, new byte[32], out var tokenLength)
        && tokenLength == 32
        && DateTimeOffset.UtcNow - session.CreatedAtUtc <= SessionLifetime;

    private static bool HasValidToken(HttpListenerRequest request, string expectedToken)
    {
        var suppliedToken = request.QueryString["token"] ?? request.Headers["X-Transfer-Token"];
        if (string.IsNullOrWhiteSpace(suppliedToken) || string.IsNullOrWhiteSpace(expectedToken)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(expectedToken),
                Convert.FromBase64String(suppliedToken));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        var normalized = relativePath.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..");
    }

    private static string GetServedFileKey(string sessionId, string relativePath) =>
        $"{sessionId}\u001f{relativePath.Replace('\\', '/')}";

    private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 is > MaxRequestBodyBytes)
            throw new InvalidDataException("La requête est trop volumineuse.");

        using var reader = new StreamReader(request.InputStream);
        var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (payload.Length > MaxRequestBodyBytes)
            throw new InvalidDataException("La requête est trop volumineuse.");
        return payload;
    }
}
