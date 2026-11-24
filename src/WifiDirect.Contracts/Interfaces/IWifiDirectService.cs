using WindowsWifiLibrary.Models;

namespace WindowsWifiLibrary.Interfaces;

/// <summary>
/// Platform-neutral Wi-Fi Direct contract. Implementations own native request
/// objects internally; UI and orchestration only receive stable IDs and metadata.
/// </summary>
public interface IWifiDirectService : IDisposable
{
    WifiDirectStatus CurrentStatus { get; }
    WifiDirectPublicherStatus PublisherStatus { get; }
    WifiDirectWatcherStatus WatcherStatus { get; }

    event EventHandler<WifiDirectStatusEventArgs>? StatusChanged;
    event EventHandler<WifiDirectPublicherStatusEventArgs>? PublicherStatusChanged;
    event EventHandler<WifiDirectWatcherStatusEventArgs>? WatcherStatusChanged;
    event EventHandler<WifiDirectPeerEventArgs>? PeerDiscovered;
    event EventHandler<WifiDirectPeerEventArgs>? PeerUpdated;
    event EventHandler<WifiDirectPeerEventArgs>? PeerRemoved;
    event EventHandler<WifiDirectInvitationEventArgs>? InvitationReceived;
    event EventHandler<WifiDirectConnectionEventArgs>? ConnectionEstablished;
    event EventHandler<WifiDirectPeerEventArgs>? ConnectionClosed;
    event EventHandler<WifiDirectErrorEventArgs>? Error;

    Task<bool> InitializeAsync(CancellationToken token);
    Task StartPublishingAsync(CancellationToken token);
    Task StartSubscribingAsync(CancellationToken token);
    Task StartPublisherSubscribingAsync(CancellationToken token);
    bool DisconnectToPeer(ref WifiDirectPeer? peer, CancellationToken token);
    Task<bool> ConnectToPeerAsync(WifiDirectPeer peer, CancellationToken token);
    Task<bool> AcceptInvitationAsync(string token);
    Task RejectInvitationAsync(string token);
    void Stop();
}
