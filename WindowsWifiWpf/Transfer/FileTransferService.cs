using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace WindowsWifiWpf.Transfer;

public sealed class FileTransferService : IFileTransferService
{
    public const int DefaultPort = 47825;
    private const long MaximumFileSize = 100L * 1024 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly HttpClient _httpClient = new();
    private WebApplication? _application;
    private bool _disposed;

    public FileTransferEndpoint? LocalEndpoint { get; private set; }

    public async Task<FileTransferEndpoint> StartAsync(string localAddress, string storageDirectory, string? sessionToken = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IPAddress.TryParse(localAddress, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Une adresse IPv4 locale valide est requise.", nameof(localAddress));

        Directory.CreateDirectory(storageDirectory);
        var fullStorageDirectory = Path.GetFullPath(storageDirectory);
        sessionToken ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, DefaultPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1));
        var application = builder.Build();

        application.MapPost("/api/files", async (HttpRequest request, CancellationToken requestCancellationToken) =>
        {
            if (!IsAuthorized(request, sessionToken)) return Results.Unauthorized();
            var fileName = request.Headers.ContentDisposition.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(fileName)) return Results.BadRequest("Le nom du fichier est requis.");
            var safeFileName = Path.GetFileName(fileName);
            if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeFileName)) return Results.BadRequest("Nom de fichier invalide.");
            if (request.ContentLength is > MaximumFileSize) return Results.BadRequest("Le fichier dépasse la taille maximale autorisée.");

            var destinationPath = GetSafePath(fullStorageDirectory, safeFileName);
            var temporaryPath = destinationPath + ".part";
            try
            {
                await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await request.Body.CopyToAsync(output, requestCancellationToken);
                var length = new FileInfo(temporaryPath).Length;
                if (length > MaximumFileSize) return Results.BadRequest("Le fichier dépasse la taille maximale autorisée.");
                File.Move(temporaryPath, destinationPath, overwrite: true);
                return Results.Ok(new { FileName = safeFileName, Length = length });
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        });

        application.MapGet("/api/files/{fileName}", (HttpRequest request, string fileName) =>
        {
            if (!IsAuthorized(request, sessionToken)) return Results.Unauthorized();
            var safeFileName = Path.GetFileName(fileName);
            if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeFileName)) return Results.BadRequest("Nom de fichier invalide.");
            var path = GetSafePath(fullStorageDirectory, safeFileName);
            return !File.Exists(path) ? Results.NotFound() : Results.File(path, "application/octet-stream", safeFileName, enableRangeProcessing: true);
        });

        await application.StartAsync(cancellationToken);
        var addressText = application.Urls.Single(url => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        var endpoint = new FileTransferEndpoint(new Uri(addressText), sessionToken);
        lock (_sync)
        {
            _application = application;
            LocalEndpoint = endpoint;
        }
        return endpoint;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        WebApplication? application;
        lock (_sync)
        {
            application = _application;
            _application = null;
            LocalEndpoint = null;
        }
        if (application is not null)
        {
            await application.StopAsync(cancellationToken);
            await application.DisposeAsync();
        }
    }

    public async Task SendFileAsync(FileTransferEndpoint endpoint, string filePath, IProgress<FileTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) throw new FileNotFoundException("Fichier source introuvable.", filePath);
        if (fileInfo.Length > MaximumFileSize) throw new InvalidOperationException("Le fichier dépasse la taille maximale autorisée.");

        await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new ProgressStreamContent(input, fileInfo.Length, progress, cancellationToken);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileInfo.Name };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseAddress, "api/files")) { Content = content };
        request.Headers.TryAddWithoutValidation("X-Session-Token", endpoint.SessionToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> DownloadFileAsync(FileTransferEndpoint endpoint, string fileName, string destinationDirectory, IProgress<FileTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(safeFileName)) throw new ArgumentException("Nom de fichier invalide.", nameof(fileName));
        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = GetSafePath(Path.GetFullPath(destinationDirectory), safeFileName);
        var temporaryPath = destinationPath + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseAddress, $"api/files/{Uri.EscapeDataString(safeFileName)}"));
        request.Headers.TryAddWithoutValidation("X-Session-Token", endpoint.SessionToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await CopyWithProgressAsync(input, output, response.Content.Headers.ContentLength ?? -1, progress, cancellationToken);
        File.Move(temporaryPath, destinationPath, overwrite: true);
        return destinationPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _httpClient.Dispose();
    }

    private static bool IsAuthorized(HttpRequest request, string expectedToken) => request.Headers.TryGetValue("X-Session-Token", out var token) && string.Equals(token.ToString(), expectedToken, StringComparison.Ordinal);

    private static string GetSafePath(string rootDirectory, string fileName)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Le chemin est hors du dossier autorisé.");
        return path;
    }

    private static async Task CopyWithProgressAsync(Stream input, Stream output, long totalBytes, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long transferred = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            transferred += read;
            progress?.Report(new FileTransferProgress(transferred, totalBytes));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FileTransferService));
    }

    private sealed class ProgressStreamContent(Stream source, long length, IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken) : HttpContent
    {
        protected override bool TryComputeLength(out long contentLength)
        {
            contentLength = length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyWithProgressAsync(source, stream, length, progress, cancellationToken);
    }
}
