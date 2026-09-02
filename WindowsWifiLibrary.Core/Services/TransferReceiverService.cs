using System.Net.Http.Json;
using WindowsWifiLibrary.Core.Models;

namespace WindowsWifiLibrary.Core.Services;

public sealed class TransferReceiverService
{
    private readonly HttpClient _httpClient;

    public TransferReceiverService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<TransferSessionMetadata?> GetSessionAsync(string senderBaseUrl, string sessionId, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(BuildUrl(senderBaseUrl, $"/api/transfer/session/{Uri.EscapeDataString(sessionId)}"), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<TransferSessionMetadata>(cancellationToken: cancellationToken);
    }

    public async Task<TransferDecision?> AcceptTransferAsync(string senderBaseUrl, string sessionId, string? destinationFolder, CancellationToken cancellationToken)
    {
        var payload = new { sessionId, destinationFolder };
        var response = await _httpClient.PostAsJsonAsync(BuildUrl(senderBaseUrl, "/api/transfer/accept"), payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TransferDecision>(cancellationToken: cancellationToken);
    }

    public async Task DownloadSessionAsync(string senderBaseUrl, TransferSessionMetadata session, string destinationFolder, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        long totalTransferred = 0;
        await DownloadItemsAsync(senderBaseUrl, session, destinationFolder, session.Items, progress, ref totalTransferred, cancellationToken);
    }

    private async Task DownloadItemsAsync(string senderBaseUrl, TransferSessionMetadata session, string destinationFolder, IEnumerable<TransferItemMetadata> items, IProgress<FileTransferProgress> progress, ref long totalTransferred, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.IsDirectory)
            {
                foreach (var child in item.Children)
                {
                    await DownloadItemsAsync(senderBaseUrl, session, destinationFolder, new[] { child }, progress, ref totalTransferred, cancellationToken);
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

            await DownloadFileAsync(senderBaseUrl, session, relativePath, targetPath, item.Size, progress, ref totalTransferred, cancellationToken);
        }
    }

    private async Task DownloadFileAsync(string senderBaseUrl, TransferSessionMetadata session, string relativePath, string destinationPath, long expectedLength, IProgress<FileTransferProgress> progress, ref long totalTransferred, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(BuildDownloadUrl(senderBaseUrl, session.SessionId, relativePath), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var destinationStream = File.Create(destinationPath);
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[81920];
        var downloaded = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            downloaded += bytesRead;
            totalTransferred += bytesRead;
            progress.Report(new FileTransferProgress
            {
                BytesTransferred = totalTransferred,
                TotalBytes = session.TotalSize,
                CurrentFileName = relativePath
            });
        }

        if (expectedLength > 0 && downloaded != expectedLength)
        {
            throw new InvalidOperationException($"Le fichier '{destinationPath}' n'a pas été entièrement téléchargé.");
        }
    }

    private static string BuildUrl(string baseUrl, string relativePath)
    {
        return baseUrl.TrimEnd('/') + relativePath;
    }

    private static string BuildDownloadUrl(string baseUrl, string sessionId, string relativePath)
    {
        var encodedPath = Uri.EscapeDataString(relativePath);
        return $"{baseUrl.TrimEnd('/')}/api/transfer/download?sessionId={Uri.EscapeDataString(sessionId)}&path={encodedPath}";
    }
}
