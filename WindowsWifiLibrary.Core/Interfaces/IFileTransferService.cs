using WindowsWifiLibrary.Core.Models;

namespace WindowsWifiLibrary.Core.Interfaces;

public interface IFileTransferService
{
    event EventHandler<PeerDevice> DeviceDiscovered;
    event EventHandler<TransferSessionMetadata> IncomingTransferRequested;

    Task StartListeningAsync(string localIp, int port, CancellationToken cancellationToken);
    Task StopListeningAsync();

    Task<TransferValidationResult> ValidateTransferAsync(IEnumerable<string> paths, CancellationToken cancellationToken);
    Task<TransferSessionMetadata> PrepareMetadataAsync(IEnumerable<string> paths, string localIp, int port);
    Task<TransferDecision> AcceptTransferAsync(TransferSessionMetadata session, string? destinationFolder, CancellationToken cancellationToken);
    Task<bool> RequestPairingAsync(PeerDevice targetDevice, TransferSessionMetadata metadata, CancellationToken cancellationToken);
    Task DownloadSessionAsync(TransferSessionMetadata session, string destinationFolder, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken);
    Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken);
}