using LibraryMI.Interfaces;
using LibraryMI.Models;
using System.Net;
using System.Net.Sockets;

namespace TcpApiTransfert;
public class TcpClienter: IFileTransferService
{
    private TcpListener? Client;
    private Stream? StreamClient;
    private TcpListener? Cast;
    private Stream? StreamCast;

    public event EventHandler? ClientConnected;
    public event EventHandler? ClientDisconnected;

    public event EventHandler<TransferSessionMetadata> IncomingTransferRequested;


    
    private void GetTokenSource(ref CancellationTokenSource? source)
    {
        if (source != null)
        {
            if(!source.Token.IsCancellationRequested)
            {
                source.Cancel();
            }
        }
        source = new();
    }
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    public Task StartListeningAsync(string localIp, int port, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>{ await StartAsync(localIp, port, cancellationToken); });
    }

    private async Task StartAsync(string localIp, int port, CancellationToken token)
    {
        if(Cast != null)
        {
            await StopListeningAsync();
        }
        token.ThrowIfCancellationRequested();
        Cast = new(IPAddress.Parse(localIp),port);

        Cast.Start();

        while (!token.IsCancellationRequested)
        {
            
        }

    }

    public Task StopListeningAsync()
    {
        Cast?.Stop();
        Cast?.Dispose();
        return Task.CompletedTask;
    }

    public Task<TransferValidationResult> ValidateTransferAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<TransferSessionMetadata> PrepareMetadataAsync(IEnumerable<string> paths, string localIp, int port)
    {
        throw new NotImplementedException();
    }

    public Task<TransferDecision> AcceptTransferAsync(TransferSessionMetadata session, string? destinationFolder, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<bool> RequestPairingAsync(PeerDevice targetDevice, TransferSessionMetadata metadata, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task DownloadSessionAsync(TransferSessionMetadata session, string destinationFolder, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
    
    public void Stop() 
    {
        Cast?.Stop();
        Cast?.Dispose();
        Client?.Stop();
        Client?.Dispose();
    }
}
