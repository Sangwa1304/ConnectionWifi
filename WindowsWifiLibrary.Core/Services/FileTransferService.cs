using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WindowsWifiLibrary.Core.Interfaces;
using WindowsWifiLibrary.Core.Models;

namespace WindowsWifiLibrary.Core.Services;

public class FileTransferService : IFileTransferService
{
    public event EventHandler<TransferSessionMetadata>? IncomingTransferRequested;

    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private readonly Dictionary<string, string> _servedFiles = []; // Mappe le RelativePath au FullLocalPath
    private readonly Dictionary<string, TransferSessionMetadata> _activeSessions = new(StringComparer.OrdinalIgnoreCase);

    public async Task StartListeningAsync(string localIp, int port, CancellationToken cancellationToken)
    {
        if (_listener?.IsListening is true) return;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{localIp}:{port}/");
        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        try
        {
            while (!_listenerCts.Token.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleIncomingRequestAsync(context), _listenerCts.Token);
            }
        }
        catch (HttpListenerException) { }
    }

    public Task<TransferValidationResult> ValidateTransferAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new TransferValidationResult();
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var trimmedPath = rawPath.Trim();
            if (!File.Exists(trimmedPath) && !Directory.Exists(trimmedPath))
            {
                result.Errors.Add($"Le chemin '{trimmedPath}' est introuvable ou invalide.");
                continue;
            }

            var canonicalPath = Path.GetFullPath(trimmedPath);
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

    public Task<TransferDecision> AcceptTransferAsync(TransferSessionMetadata session, string? destinationFolder, CancellationToken cancellationToken)
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
        return Task.FromResult(decision);
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
        _listener?.Stop();
        _listener = null;
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
            if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/api/transfer/request")
            {
                using var reader = new StreamReader(request.InputStream);
                var payload = await reader.ReadToEndAsync();
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
            else if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/api/transfer/download")
            {
                string relativePath = request.QueryString["path"] ?? string.Empty;
                if (_servedFiles.TryGetValue(relativePath, out var fullPath) && File.Exists(fullPath))
                {
                    await ServeFileChunkAsync(request, response, fullPath);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
            else if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/api/transfer/accept")
            {
                response.StatusCode = (int)HttpStatusCode.Accepted;
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }
        finally
        {
            response.Close();
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

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(startByte, SeekOrigin.Begin);

        var buffer = new byte[81920];
        long totalSent = 0;
        while (totalSent < contentLength)
        {
            int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, contentLength - totalSent)));
            if (bytesRead == 0) break;
            await response.OutputStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalSent += bytesRead;
        }
    }

    public Task<TransferSessionMetadata> PrepareMetadataAsync(IEnumerable<string> paths, string localIp, int port)
    {
        var session = new TransferSessionMetadata { SenderIp = localIp, SenderPort = port, SessionState = TransferSessionState.Created };
        _servedFiles.Clear();

        var validation = ValidateTransferAsync(paths, CancellationToken.None).GetAwaiter().GetResult();
        if (!validation.IsValid)
        {
            session.ErrorMessage = string.Join("; ", validation.Errors);
            session.SessionState = TransferSessionState.Failed;
            session.NegotiationStatus = TransferNegotiationStatus.Error;
            return Task.FromResult(session);
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

        session.SessionId = string.IsNullOrWhiteSpace(session.SessionId) ? Guid.NewGuid().ToString() : session.SessionId;
        _activeSessions[session.SessionId] = session;
        return Task.FromResult(session);
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
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync($"http://{targetDevice.IpAddress}:{targetDevice.Port}/api/transfer/request", metadata, cancellationToken);
        return response.IsSuccessStatusCode;
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
                    await ProcessItemAsync(child);
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
                globalBytesTransferred += bytesRead;
                progress.Report(new FileTransferProgress
                {
                    BytesTransferred = globalBytesTransferred,
                    TotalBytes = session.TotalSize,
                    CurrentFileName = item.RelativePath
                });
            });

            await DownloadFileAsync($"{baseUrl}?path={Uri.EscapeDataString(item.RelativePath)}", targetPath, item.Size, cancellationToken, fileProgress);
        }

        foreach (var item in session.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessItemAsync(item);
        }
    }

    private static async Task DownloadFileAsync(string url, string destinationPath, long fileSize, CancellationToken token, IProgress<long> onProgress)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        await using var destinationStream = File.Create(destinationPath);
        await using var sourceStream = await response.Content.ReadAsStreamAsync(token);

        var buffer = new byte[81920];
        int bytesRead;
        long downloaded = 0;

        while ((bytesRead = await sourceStream.ReadAsync(buffer, token)) > 0)
        {
            token.ThrowIfCancellationRequested();
            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
            downloaded += bytesRead;
            onProgress.Report(bytesRead);
        }

        if (downloaded != fileSize)
        {
            throw new InvalidOperationException($"Le fichier '{destinationPath}' n'a pas été entièrement téléchargé.");
        }
    }
}