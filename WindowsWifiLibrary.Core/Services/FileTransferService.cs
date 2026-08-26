using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WindowsWifiLibrary.Core.Interfaces;
using WindowsWifiLibrary.Core.Models;

namespace WindowsWifiLibrary.Core.Services;

public class FileTransferService : IFileTransferService 
{ 
    public event EventHandler<PeerDevice>? DeviceDiscovered; 
    public event EventHandler<TransferSessionMetadata>? IncomingTransferRequested; 

    private HttpListener? _listener; 
    private CancellationTokenSource? _listenerCts; 
    private readonly Dictionary<string, string> _servedFiles = []; // Mappe le RelativePath au FullLocalPath 

    public async Task StartListeningAsync(string localIp, int port, CancellationToken cancellationToken) 
    { 
        if (_listener != null && _listener.IsListening) return; 

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

    public Task StopListeningAsync() 
    { 
        _listenerCts?.Cancel(); 
        _listener?.Stop(); 
        _listener = null; 
        _servedFiles.Clear();
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
                var metadata = JsonSerializer.Deserialize<TransferSessionMetadata>(await reader.ReadToEndAsync()); 
                if (metadata != null) 
                { 
                    IncomingTransferRequested?.Invoke(this, metadata); 
                    response.StatusCode = (int)HttpStatusCode.Accepted; 
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
        } 
        finally 
        { 
            response.Close(); 
        } 
    } 

    private async Task ServeFileChunkAsync(HttpListenerRequest request, HttpListenerResponse response, string filePath) 
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

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read); 
        fs.Seek(startByte, SeekOrigin.Begin); 

        var buffer = new byte[81920]; 
        long totalSent = 0; 
        while (totalSent < contentLength) 
        { 
            int bytesRead = await fs.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, contentLength - totalSent)); 
            if (bytesRead == 0) break; 
            await response.OutputStream.WriteAsync(buffer, 0, bytesRead); 
            totalSent += bytesRead; 
        } 
    } 

    public Task<TransferSessionMetadata> PrepareMetadataAsync(IEnumerable<string> paths, string localIp, int port) 
    { 
        var session = new TransferSessionMetadata { SenderIp = localIp, SenderPort = port }; 
        _servedFiles.Clear(); 

        foreach (var path in paths) 
        { 
            if (File.Exists(path)) 
            { 
                var fileInfo = new FileInfo(path); 
                session.Items.Add(new TransferItemMetadata { RelativePath = fileInfo.Name, IsDirectory = false, Size = fileInfo.Length }); 
                session.TotalSize += fileInfo.Length; 
                session.TotalFiles++; 
                _servedFiles[fileInfo.Name] = fileInfo.FullName; 
            } 
            else if (Directory.Exists(path)) 
            { 
                var dirInfo = new DirectoryInfo(path); 
                session.Items.Add(BuildDirectoryMetadata(dirInfo, dirInfo.Name, session)); 
            } 
        } 
        return Task.FromResult(session); 
    } 

    private TransferItemMetadata BuildDirectoryMetadata(DirectoryInfo dir, string relativeBase, TransferSessionMetadata session) 
    { 
        var dirMeta = new TransferItemMetadata { RelativePath = relativeBase, IsDirectory = true }; 
        foreach (var file in dir.GetFiles()) 
        { 
            string relPath = $"{relativeBase}/{file.Name}"; 
            dirMeta.Children.Add(new TransferItemMetadata { RelativePath = relPath, IsDirectory = false, Size = file.Length }); 
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
        long globalBytesTransferred = 0; 
        string baseUrl = $"http://{session.SenderIp}:{session.SenderPort}/api/transfer/download"; 

        async Task ProcessItemAsync(TransferItemMetadata item) 
        { 
            string targetPath = Path.Combine(destinationFolder, item.RelativePath); 
            if (item.IsDirectory) 
            { 
                Directory.CreateDirectory(targetPath); 
                foreach (var child in item.Children) await ProcessItemAsync(child); 
            } 
            else 
            { 
                await DownloadFileMultiThreadedAsync($"{baseUrl}?path={Uri.EscapeDataString(item.RelativePath)}", targetPath, item.Size, 4, cancellationToken, read =>  
                { 
                    globalBytesTransferred += read; 
                    progress?.Report(new FileTransferProgress { BytesTransferred = globalBytesTransferred, TotalBytes = session.TotalSize, CurrentFileName = item.RelativePath }); 
                }); 
            } 
        } 
        foreach (var item in session.Items) await ProcessItemAsync(item); 
    } 

    private async Task DownloadFileMultiThreadedAsync(string url, string destinationPath, long fileSize, int threads, CancellationToken token, Action<int> onProgress) 
    { 
        using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None); 
        fs.SetLength(fileSize); 
         
        long chunkSize = fileSize / threads; 
        var tasks = new List<Task>(); 

        for (int i = 0; i < threads; i++) 
        { 
            long start = i * chunkSize; 
            long end = (i == threads - 1) ? fileSize - 1 : start + chunkSize - 1; 

            tasks.Add(Task.Run(async () => 
            { 
                using var client = new HttpClient(); 
                var request = new HttpRequestMessage(HttpMethod.Get, url); 
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end); 
                 
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); 
                using var stream = await response.Content.ReadAsStreamAsync(token); 
                using var filePartStream = new FileStream(destinationPath, FileMode.Open, FileAccess.Write, FileShare.Write); 
                filePartStream.Seek(start, SeekOrigin.Begin); 

                var buffer = new byte[81920]; 
                int read; 
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0) 
                { 
                    token.ThrowIfCancellationRequested(); 
                    lock (filePartStream) filePartStream.Write(buffer, 0, read); 
                    lock (onProgress) onProgress(read); 
                } 
            }, token)); 
        } 
        await Task.WhenAll(tasks); 
    } 
} 