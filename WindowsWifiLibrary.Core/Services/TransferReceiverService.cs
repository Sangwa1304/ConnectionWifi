using System.Net.Http.Json;
using WindowsWifiLibrary.Core.Models;

namespace WindowsWifiLibrary.Core.Services;

public sealed class TransferReceiverService
{
    private readonly HttpClient _httpClient;
    private long _totalTransferred = 0;

    public TransferReceiverService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<TransferSessionMetadata?> GetSessionAsync(string senderBaseUrl, string sessionId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(BuildUrl(senderBaseUrl, $"/api/transfer/session/{Uri.EscapeDataString(sessionId)}"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadFromJsonAsync<TransferSessionMetadata>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferDecision?> AcceptTransferAsync(string senderBaseUrl, string sessionId, string? destinationFolder, CancellationToken cancellationToken)
    {
        var payload = new { sessionId, destinationFolder };
        var response = await _httpClient.PostAsJsonAsync(BuildUrl(senderBaseUrl, "/api/transfer/accept"), payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TransferDecision>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadSessionAsync(string senderBaseUrl, TransferSessionMetadata session, string destinationFolder, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));

        Interlocked.Exchange(ref _totalTransferred, 0);
        await DownloadItemsAsync(senderBaseUrl, session, destinationFolder, session.Items, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadItemsAsync(string senderBaseUrl, TransferSessionMetadata session, string destinationFolder, IEnumerable<TransferItemMetadata> items, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.IsDirectory)
            {
                foreach (var child in item.Children)
                {
                    await DownloadItemsAsync(senderBaseUrl, session, destinationFolder, [child], progress, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            var relativePath = item.RelativePath.Replace('\\', '/');
            var targetPath = Path.Combine(destinationFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
            
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await DownloadFileAsync(senderBaseUrl, session, relativePath, targetPath, item.Size, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DownloadFileAsync(string senderBaseUrl, TransferSessionMetadata session, string relativePath, string destinationPath, long expectedLength, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(BuildDownloadUrl(senderBaseUrl, session.SessionId, relativePath), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var destinationStream = File.Create(destinationPath);
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[81920];
        var downloaded = 0L;

        var reportContainer = new FileTransferProgress
        {
            TotalBytes = session.TotalSize,
            CurrentFileName = relativePath
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0) break;

            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            downloaded += bytesRead;
            
            long contextTransferred = Interlocked.Add(ref _totalTransferred, bytesRead);
            
            reportContainer.BytesTransferred = contextTransferred;
            progress.Report(reportContainer);
        }

        if (expectedLength > 0 && downloaded != expectedLength)
        {
            throw new InvalidOperationException($"Le fichier '{destinationPath}' n'a pas été entièrement téléchargé.");
        }
    }

    private static string BuildUrl(string baseUrl, string relativePath) => baseUrl.TrimEnd('/') + relativePath;

    private static string BuildDownloadUrl(string baseUrl, string sessionId, string relativePath)
    {
        return $"{baseUrl.TrimEnd('/')}/api/transfer/download?sessionId={Uri.EscapeDataString(sessionId)}&path={Uri.EscapeDataString(relativePath)}";
    }
}
