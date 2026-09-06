using LibraryMI.Interfaces;
using LibraryMI.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WindowsWifiLibrary.Core.Models;

namespace WindowsWifiLibrary.Core.Services;

public class FileTransferService : IFileTransferService
{
    public event EventHandler<TransferSessionMetadata>? IncomingTransferRequested;

    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerLoopTask;
    private readonly HttpClient _httpClient = new();

    private readonly Dictionary<string, TaskCompletionSource<TransferDecision>> _pairingRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _servedFiles = []; 
    private readonly Dictionary<string, TransferSessionMetadata> _activeSessions = new(StringComparer.OrdinalIgnoreCase);

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

        var decision = new TransferDecision
        {
            Accepted = true,
            DestinationFolder = string.IsNullOrWhiteSpace(destinationFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Wi-Fi P2P")
                : destinationFolder,
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
            _activeSessions.Remove(sessionId);
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
                using var reader = new StreamReader(request.InputStream);
                var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                var metadata = JsonSerializer.Deserialize<TransferSessionMetadata>(payload);
                if (metadata != null)
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
                using var reader = new StreamReader(request.InputStream);
                var payload = await reader.ReadToEndAsync().ConfigureAwait(false);
                var decision = JsonSerializer.Deserialize<TransferDecision>(payload);
                string sessionId = request.QueryString["sessionId"] ?? string.Empty;

                if (decision != null && !string.IsNullOrWhiteSpace(sessionId))
                {
                    if (_pairingRequests.TryGetValue(sessionId, out var tcs))
                    {
                        tcs.TrySetResult(decision);
                        _pairingRequests.Remove(sessionId);
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
                string sessionId = cleanPath.Substring("/api/transfer/session/".Length);
                if (!string.IsNullOrEmpty(sessionId) && _activeSessions.TryGetValue(sessionId, out var session))
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
                string relativePath = request.QueryString["path"] ?? string.Empty;
                if (_servedFiles.TryGetValue(relativePath, out string? fullLocalPath) && File.Exists(fullLocalPath))
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
            var parts = rangeHeader.Replace("bytes=", "").Split('-');
            if (long.TryParse(parts[0], out var pStart)) startByte = pStart;
            if (parts.Length > 1 && long.TryParse(parts[1], out var pEnd)) endByte = pEnd;
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
        var session = new TransferSessionMetadata { SenderIp = localIp, SenderPort = port, SessionState = TransferSessionState.Created };
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
                _servedFiles[item.RelativePath] = fileInfo.FullName;
            }
            else if (Directory.Exists(normalizedPath))
            {
                var directoryInfo = new DirectoryInfo(normalizedPath);
                session.Items.Add(BuildDirectoryMetadata(directoryInfo, directoryInfo.Name, session));
            }
        }

        _activeSessions[session.SessionId] = session;
        return session;
    }

    private TransferItemMetadata BuildDirectoryMetadata(DirectoryInfo dir, string relativeBase, TransferSessionMetadata session)
    {
        var dirMeta = new TransferItemMetadata { RelativePath = relativeBase, FullLocalPath = dir.FullName, IsDirectory = true };

        foreach (var file in dir.GetFiles())
        {
            string relPath = $"{relativeBase}/{file.Name}";
            var item = new TransferItemMetadata { RelativePath = relPath, FullLocalPath = file.FullName, IsDirectory = false, Size = file.Length };
            dirMeta.Children.Add(item);
            session.TotalSize += file.Length;
            session.TotalFiles++;
            _servedFiles[relPath] = file.FullName;
        }

        foreach (var subDir in dir.GetDirectories())
        {
            dirMeta.Children.Add(BuildDirectoryMetadata(subDir, $"{relativeBase}/{subDir.Name}", session));
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
                _pairingRequests.Remove(metadata.SessionId);
                metadata.SessionState = TransferSessionState.Failed;
                return false;
            }

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                TransferDecision decision = await tcs.Task.ConfigureAwait(false);
                if (decision.Accepted)
                {
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
            _pairingRequests.Remove(metadata.SessionId);
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
            string targetPath = Path.Combine(destinationFolder, sanitizedRelativePath);

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

            await DownloadFileAsync($"{baseUrl}?path={Uri.EscapeDataString(item.RelativePath)}", targetPath, item.Size, cancellationToken, fileProgress).ConfigureAwait(false);
        }

        foreach (var item in session.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessItemAsync(item).ConfigureAwait(false);
        }
    }

    private async Task DownloadFileAsync(string url, string destinationPath, long fileSize, CancellationToken token, IProgress<long> onProgress)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var destinationStream = File.Create(destinationPath);
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
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _listenerCts?.Dispose();
    }
}
