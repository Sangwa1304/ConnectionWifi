namespace WindowsWifiWpf.Transfer;

public interface IFileTransferService : IAsyncDisposable
{
    FileTransferEndpoint? LocalEndpoint { get; }

    Task<FileTransferEndpoint> StartAsync(string localAddress, string storageDirectory, string? sessionToken = null, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendFileAsync(FileTransferEndpoint endpoint, string filePath, IProgress<FileTransferProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<string> DownloadFileAsync(FileTransferEndpoint endpoint, string fileName, string destinationDirectory, IProgress<FileTransferProgress>? progress = null, CancellationToken cancellationToken = default);
}
