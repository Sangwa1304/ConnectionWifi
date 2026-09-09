using LibraryMI.Interfaces;
using LibraryMI.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace WindowsWifiLibrary.Core.Services;

internal static class CertificateHelper
{
    public static X509Certificate2 GetOrCreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Wi-Fi-P2P-Transfer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

        var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
#pragma warning disable SYSLIB0057 // Le type ou le membre est obsolète
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057 // Le type ou le membre est obsolète
    }
}
public sealed class FileTransferService : IFileTransferService
{
    private const int MaxFilesPerSession = 10_000;
    private const int MaxDirectoryDepth = 32;
    private const int MaxConcurrentSegments = 4; // Nombre de flux parallèles par fichier
    private const long SegmentThreshold = 5 * 1024 * 1024; // Fichiers > 5Mo segmentés
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);

    public event EventHandler<TransferSessionMetadata>? IncomingTransferRequested;

    private WebApplication? _webApp;
    private readonly HttpClient _httpClient;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<TransferDecision>> _pairingRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _servedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TransferSessionMetadata> _activeSessions = new(StringComparer.OrdinalIgnoreCase);

    public FileTransferService()
    {
        // Configuration HTTP/3 (QUIC) pour le client
        var handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                // signature attend (object?, X509Certificate?, X509Chain?, SslPolicyErrors) => bool
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true, // Tolérance P2P local
                ApplicationProtocols = new List<System.Net.Security.SslApplicationProtocol>
                {
                    System.Net.Security.SslApplicationProtocol.Http3
                }
            }
        };
        _httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task StartListeningAsync(string localIp, int port, CancellationToken cancellationToken)
    {
        if (_webApp != null) return;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            var ipAddress = IPAddress.TryParse(localIp, out var ip) ? ip : IPAddress.Any;
            options.Listen(ipAddress, port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                listenOptions.UseHttps(CertificateHelper.GetOrCreateSelfSignedCertificate());
            });
        });

        _webApp = builder.Build();
        ConfigureEndpoints(_webApp);

        await _webApp.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ConfigureEndpoints(WebApplication app)
    {
        Func<TransferSessionMetadata, IResult> handleRequest = (TransferSessionMetadata metadata) =>
        {
            if (metadata != null && IsValidSession(metadata))
            {
                _activeSessions[metadata.SessionId] = metadata;
                metadata.SessionState = TransferSessionState.Negotiating;
                IncomingTransferRequested?.Invoke(this, metadata);
                return Results.Accepted();
            }
            return Results.BadRequest();
        };
        app.MapPost("/api/transfer/request", handleRequest);

        Func<TransferDecision, HttpContext, IResult> handleAccept = (TransferDecision decision, HttpContext ctx) =>
        {
            string sessionId = ctx.Request.Query["sessionId"].ToString();
            if (decision != null && !string.IsNullOrWhiteSpace(sessionId) &&
                _activeSessions.TryGetValue(sessionId, out var activeSession) &&
                HasValidToken(decision.SessionToken, activeSession.SessionToken))
            {
                if (_pairingRequests.TryGetValue(sessionId, out var tcs))
                {
                    tcs.TrySetResult(decision);
                    _pairingRequests.TryRemove(sessionId, out _);
                }
                return Results.Ok();
            }
            return Results.BadRequest();
        };
        app.MapPost("/api/transfer/accept", handleAccept);

        Func<string, HttpContext, IResult> handleGetSession = (string sessionId, HttpContext ctx) =>
        {
            if (_activeSessions.TryGetValue(sessionId, out var session) &&
                HasValidToken(ctx.Request.Query["token"], session.SessionToken))
            {
                return Results.Ok(session);
            }
            return Results.NotFound();
        };
        app.MapGet("/api/transfer/session/{sessionId}", handleGetSession);

        Func<HttpContext, IResult> handleDownload = (HttpContext ctx) =>
        {
            string sessionId = ctx.Request.Query["sessionId"].ToString();
            string relativePath = ctx.Request.Query["path"].ToString();
            string token = ctx.Request.Query["token"].ToString() ?? ctx.Request.Headers["X-Transfer-Token"].ToString();

            if (_activeSessions.TryGetValue(sessionId, out var session) &&
                HasValidToken(token, session.SessionToken) &&
                _servedFiles.TryGetValue(GetServedFileKey(sessionId, relativePath), out string? fullLocalPath) &&
                File.Exists(fullLocalPath))
            {
                   
                // utiliser Results.File (signature disponible)
                return Results.File(fullLocalPath, "application/octet-stream", null, null, null, enableRangeProcessing: true);
            }
            return Results.NotFound();
        };
        app.MapGet("/api/transfer/download", handleDownload);
    }

    public async Task<bool> RequestPairingAsync(PeerDevice targetDevice, TransferSessionMetadata metadata, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<TransferDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pairingRequests[metadata.SessionId] = tcs;

        try
        {
            metadata.SessionState = TransferSessionState.Negotiating;
            string url = $"https://{targetDevice.IpAddress}:{targetDevice.Port}/api/transfer/request";
            var response = await _httpClient.PostAsJsonAsync(url, metadata, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _pairingRequests.TryRemove(metadata.SessionId, out _);
                metadata.SessionState = TransferSessionState.Failed;
                return false;
            }

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                TransferDecision decision = await tcs.Task.ConfigureAwait(false);
                if (decision.Accepted && HasValidToken(decision.SessionToken, metadata.SessionToken))
                {
                    metadata.SessionState = TransferSessionState.ReadyToSend;
                    metadata.NegotiationStatus = TransferNegotiationStatus.Accepted;
                    metadata.TargetFolder = decision.DestinationFolder;
                    return true;
                }

                metadata.SessionState = TransferSessionState.Cancelled;
                metadata.NegotiationStatus = TransferNegotiationStatus.Refused;
                return false;
            }
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

    public async Task<TransferDecision> AcceptTransferAsync(TransferSessionMetadata session, string? destinationFolder, CancellationToken cancellationToken)
    {
        var destination = string.IsNullOrWhiteSpace(destinationFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Wi-Fi P2P")
            : Path.GetFullPath(destinationFolder);
        Directory.CreateDirectory(destination);

        var decision = new TransferDecision { Accepted = true, DestinationFolder = destination, SessionToken = session.SessionToken };

        try
        {
            string url = $"https://{session.SenderIp}:{session.SenderPort}/api/transfer/accept?sessionId={Uri.EscapeDataString(session.SessionId)}";
            await _httpClient.PostAsJsonAsync(url, decision, cancellationToken).ConfigureAwait(false);
            session.SessionState = TransferSessionState.ReadyToSend;
        }
        catch
        {
            decision.Accepted = false;
        }
        return decision;
    }

    public async Task DownloadSessionAsync(TransferSessionMetadata session, string destinationFolder, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken)
    {
        long globalBytesTransferred = 0;
        string baseUrl = $"https://{session.SenderIp}:{session.SenderPort}/api/transfer/download";

        async Task ProcessItemAsync(TransferItemMetadata item)
        {
            string targetPath = Path.Combine(destinationFolder, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(targetPath);
                foreach (var child in item.Children) await ProcessItemAsync(child).ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var fileProgress = new Progress<long>(bytesRead =>
            {
                long currentTotal = Interlocked.Add(ref globalBytesTransferred, bytesRead);
                progress.Report(new FileTransferProgress { BytesTransferred = currentTotal, TotalBytes = session.TotalSize, CurrentFileName = item.RelativePath });
            });

            string url = $"{baseUrl}?sessionId={Uri.EscapeDataString(session.SessionId)}&path={Uri.EscapeDataString(item.RelativePath)}";

            if (item.Size > SegmentThreshold)
            {
                await DownloadFileSegmentedAsync(url, session.SessionToken, targetPath, item.Size, cancellationToken, fileProgress).ConfigureAwait(false);
            }
            else
            {
                await DownloadFileSingleAsync(url, session.SessionToken, targetPath, cancellationToken, fileProgress).ConfigureAwait(false);
            }
        }

        foreach (var item in session.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessItemAsync(item).ConfigureAwait(false);
        }
    }

    // --- MOTEUR DE TÉLÉCHARGEMENT SEGMENTÉ (HTTP/3 - QUIC) ---
    private async Task DownloadFileSegmentedAsync(string url, string sessionToken, string destinationPath, long fileSize, CancellationToken token, IProgress<long> onProgress)
    {
        string tempPath = destinationPath + $".{Guid.NewGuid():N}.part";
        try
        {
            // Initialiser le fichier à la bonne taille pour écritures concurrentes sécurisées
            using (var initFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                initFs.SetLength(fileSize);
            }

            long segmentSize = fileSize / MaxConcurrentSegments;
            var tasks = new List<Task>();

            // Utilisation de SafeFileHandle pour des écritures disque concurrentes sans verrous
            using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.Write, 4096, FileOptions.Asynchronous);
            var handle = fs.SafeFileHandle;

            for (int i = 0; i < MaxConcurrentSegments; i++)
            {
                long start = i * segmentSize;
                long end = (i == MaxConcurrentSegments - 1) ? fileSize - 1 : (start + segmentSize - 1);
                tasks.Add(DownloadSegmentAsync(url, sessionToken, handle, start, end, token, onProgress));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            File.Move(tempPath, destinationPath, true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private async Task DownloadSegmentAsync(string url, string sessionToken, SafeFileHandle handle, long start, long end, CancellationToken token, IProgress<long> onProgress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Transfer-Token", sessionToken);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var sourceStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[81920];
        int bytesRead;
        long currentOffset = start;

        while ((bytesRead = await sourceStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            // Écriture thread-safe au niveau du disque
            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, bytesRead), currentOffset, token).ConfigureAwait(false);
            currentOffset += bytesRead;
            onProgress.Report(bytesRead);
        }
    }

    private async Task DownloadFileSingleAsync(string url, string sessionToken, string destinationPath, CancellationToken token, IProgress<long> onProgress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Transfer-Token", sessionToken);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string tempPath = destinationPath + $".{Guid.NewGuid():N}.part";
        try
        {
            await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await using var sourceStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead), token).ConfigureAwait(false);
                onProgress.Report(bytesRead);
            }
            fs.Close();
            File.Move(tempPath, destinationPath, true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    // --- METHODES DE METADONNEES CONSERVEES[cite: 12] ---
    public Task<TransferValidationResult> ValidateTransferAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var result = new TransferValidationResult();
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? rawPath in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (!File.Exists(rawPath) && !Directory.Exists(rawPath))
            {
                result.Errors.Add($"Le chemin '{rawPath}' est introuvable.");
                continue;
            }
            string canonicalPath = Path.GetFullPath(rawPath);
            if (normalizedPaths.Add(canonicalPath)) result.NormalizedPaths.Add(canonicalPath);
        }
        if (result.NormalizedPaths.Count == 0) result.Errors.Add("Aucun fichier valide.");
        result.IsValid = result.Errors.Count == 0;
        return Task.FromResult(result);
    }

    public async Task<TransferSessionMetadata> PrepareMetadataAsync(IEnumerable<string> paths, string localIp, int port)
    {
        var session = new TransferSessionMetadata
        {
            SenderIp = localIp,
            SenderPort = port,
            SessionToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            SessionState = TransferSessionState.Created
        };
        _servedFiles.Clear();

        var validation = await ValidateTransferAsync(paths, CancellationToken.None).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            session.SessionState = TransferSessionState.Failed;
            return session;
        }

        foreach (var normalizedPath in validation.NormalizedPaths)
        {
            if (File.Exists(normalizedPath))
            {
                var fileInfo = new FileInfo(normalizedPath);
                var item = new TransferItemMetadata { RelativePath = fileInfo.Name, FullLocalPath = fileInfo.FullName, IsDirectory = false, Size = fileInfo.Length };
                session.Items.Add(item);
                session.TotalSize += fileInfo.Length;
                _servedFiles[GetServedFileKey(session.SessionId, item.RelativePath)] = fileInfo.FullName;
            }
        }
        _activeSessions[session.SessionId] = session;
        return session;
    }

    public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        _activeSessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public async Task StopListeningAsync()
    {
        if (_webApp != null)
        {
            await _webApp.StopAsync().ConfigureAwait(false);
            await _webApp.DisposeAsync().ConfigureAwait(false);
            _webApp = null;
        }
        _servedFiles.Clear();
        _activeSessions.Clear();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        StopListeningAsync().GetAwaiter().GetResult();
    }

    private static bool IsValidSession(TransferSessionMetadata session) => !string.IsNullOrWhiteSpace(session.SessionId);
    private static bool HasValidToken(string? provided, string expected) => provided != null && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(provided), Convert.FromBase64String(expected));
    private static string GetServedFileKey(string sessionId, string relativePath) => $"{sessionId}\u001f{relativePath.Replace('\\', '/')}";
}