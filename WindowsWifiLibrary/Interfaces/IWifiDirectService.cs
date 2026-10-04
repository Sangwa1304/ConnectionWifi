using WindowsWifiLibrary.Models;

namespace WindowsWifiLibrary.Interfaces; 

public interface IWifiDirectService : IDisposable 
{ 
    WifiDirectStatus CurrentStatus { get; }
    WifiDirectPublicherStatus PublisherStatus { get; }
    WifiDirectWatcherStatus WatcherStatus { get; }

    event EventHandler<WifiDirectStatusEventArgs>? StatusChanged; 
    event EventHandler<WifiDirectPeerEventArgs>? PeerDiscovered; 
    event EventHandler<WifiDirectPeerEventArgs>? PeerUpdated; 
    event EventHandler<WifiDirectPeerEventArgs>? PeerRemoved; 
    event EventHandler<WifiDirectInvitationEventArgs>? InvitationReceived; 
    event EventHandler<WifiDirectConnectionEventArgs>? ConnectionEstablished; 
    event EventHandler<WifiDirectPeerEventArgs>? ConnectionClosed; 
    event EventHandler<WifiDirectErrorEventArgs>? Error; 

    Task<bool> InitializeAsync(); 
    Task StartPublishingAsync(); 
    Task StartSubscribingAsync(); 
    Task StartPublisherSubscribingAsync();
    Task<bool> DisconnectToPeerAsync(WifiDirectPeer peer);
    Task<bool> ConnectToPeerAsync(WifiDirectPeer peer);
    Task<bool> AcceptInvitationAsync(string token);
    Task RejectInvitationAsync(string token);
    void Stop();
}
