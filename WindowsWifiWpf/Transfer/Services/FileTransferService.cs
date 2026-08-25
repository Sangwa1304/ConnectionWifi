using System.IO;
using System.Net;
using System.Net.Http;
using WindowsWifiWpf.Transfer.Interfaces;
using WindowsWifiWpf.Transfer.Models;

namespace WindowsWifiWpf.Transfer.Services;

public class FileTransferService : IFileTransferService 
{ 
    public const int DefaultPort = 5050; 
     
    private HttpListener? _listener; 
    private string? _storageDirectory; 
    private string? _sessionToken; 
    private CancellationTokenSource? _serverCancellationTokenSource; 

    public FileTransferEndpoint? LocalEndpoint { get; private set; } 

    public async Task<FileTransferEndpoint> StartAsync(string localAddress, string storageDirectory, string? sessionToken = null, CancellationToken cancellationToken = default) 
    {
        await StopAsync(cancellationToken); 

        _storageDirectory = storageDirectory ?? throw new ArgumentNullException(nameof(storageDirectory)); 
        _sessionToken = sessionToken; 
        Directory.CreateDirectory(_storageDirectory); 

        var url = $"http://{localAddress}:{DefaultPort}/"; 
        _listener = new HttpListener(); 
        _listener.Prefixes.Add(url); 

        try 
        { 
            _listener.Start(); 
        } 
        catch (HttpListenerException ex) 
        { 
            throw new InvalidOperationException($"Impossible de démarrer le serveur HTTP sur {url}.", ex); 
        } 

        LocalEndpoint = new FileTransferEndpoint(new Uri(url), sessionToken); 
        _serverCancellationTokenSource = new CancellationTokenSource(); 

        _ = Task.Run(() => ListenLoopAsync(_serverCancellationTokenSource.Token), cancellationToken); 

        return LocalEndpoint; 
    } 

    private async Task ListenLoopAsync(CancellationToken cancellationToken) 
    { 
        try 
        { 
            while (_listener != null && _listener.IsListening) 
            { 
                var contextTask = _listener.GetContextAsync(); 
                using (cancellationToken.Register(() => _listener?.Stop())) 
                { 
                    var context = await contextTask.ConfigureAwait(false); 
                    _ = Task.Run(() => HandleContextAsync(context), CancellationToken.None); 
                } 
            } 
        } 
        catch (ObjectDisposedException) { } 
        catch (HttpListenerException) { } 
    } 

    private async Task HandleContextAsync(HttpListenerContext context) 
    { 
        var request = context.Request; 
        var response = context.Response; 

        try 
        { 
            if (!string.IsNullOrEmpty(_sessionToken)) 
            { 
                var clientToken = request.Headers["X-Session-Token"]; 
                if (clientToken != _sessionToken) 
                { 
                    response.StatusCode = (int)HttpStatusCode.Unauthorized; 
                    response.Close(); 
                    return; 
                } 
            } 

            if (request.HttpMethod == "POST" || request.HttpMethod == "PUT") 
            {
                var fileName = request.Headers["X-File-Name"]; 
                if (string.IsNullOrEmpty(fileName)) 
                { 
                    fileName = $"received_{Guid.NewGuid()}.bin"; 
                } 

                fileName = Path.GetFileName(fileName); 
                var destinationPath = Path.Combine(_storageDirectory!, fileName); 

                using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true)) 
                { 
                    await request.InputStream.CopyToAsync(fs).ConfigureAwait(false); 
                } 

                response.StatusCode = (int)HttpStatusCode.OK; 
                var buffer = System.Text.Encoding.UTF8.GetBytes("Fichier reçu avec succès."); 
                response.ContentLength64 = buffer.Length; 
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false); 
            } 
            else 
            { 
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed; 
            } 
        } 
        catch (Exception) 
        { 
            response.StatusCode = (int)HttpStatusCode.InternalServerError; 
        } 
        finally 
        { 
            try { response.Close(); } catch { } 
        } 
    } 

    public async Task SendFileAsync(FileTransferEndpoint endpoint, string filePath, IProgress<FileTransferProgress>? progress = null, CancellationToken cancellationToken = default) 
    { 
        if (!File.Exists(filePath)) 
            throw new FileNotFoundException("Fichier introuvable.", filePath); 

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; 

        if (!string.IsNullOrEmpty(endpoint.SessionToken)) 
        { 
            client.DefaultRequestHeaders.Add("X-Session-Token", endpoint.SessionToken); 
        } 

        var fileName = Path.GetFileName(filePath); 
        client.DefaultRequestHeaders.Add("X-File-Name", fileName); 

        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true); 
        using var content = new ProgressStreamContent(fileStream, 81920, progress, cancellationToken); 

        var response = await client.PostAsync(endpoint.Uri, content, cancellationToken).ConfigureAwait(false); 
        response.EnsureSuccessStatusCode(); 
    } 

    public Task<string> DownloadFileAsync(FileTransferEndpoint endpoint, string fileName, string destinationDirectory, IProgress<FileTransferProgress>? progress = null, CancellationToken cancellationToken = default) 
    { 
        throw new NotImplementedException(); 
    } 

    public Task StopAsync(CancellationToken cancellationToken = default) 
    { 
        try 
        { 
            _serverCancellationTokenSource?.Cancel(); 
            _listener?.Stop(); 
            _listener?.Close(); 
        } 
        catch { } 
        finally 
        { 
            _listener = null; 
            LocalEndpoint = null; 
        } 
        return Task.CompletedTask; 
    } 

    public async ValueTask DisposeAsync() 
    { 
        await StopAsync(); 
        _serverCancellationTokenSource?.Dispose(); 
    } 
} 
