using WindowsWifiLibrary.Models;

namespace WindowsWifiLibrary.Interfaces; 

public interface IWifiDirectService : IDisposable 
{ 
    WifiDirectStatus CurrentStatus { get; } 
    bool IsHardwareSupported { get; } 
     
    event EventHandler<WifiDirectStatusEventArgs>? StatusChanged; 
    event EventHandler<WifiDirectPeerEventArgs>? PeerDiscovered; 
    event EventHandler<WifiDirectPeerEventArgs>? PeerUpdated; 
    event EventHandler<WifiDirectPeerEventArgs>? PeerRemoved; 
    event EventHandler<WifiDirectInvitationEventArgs>? InvitationReceived; 
    event EventHandler<WifiDirectConnectionEventArgs>? ConnectionEstablished; 
    event EventHandler<WifiDirectConnectionEventArgs>? ConnectionClosed; 
    event EventHandler<WifiDirectErrorEventArgs>? Error; 

    Task<bool> InitializeAsync(); 
    void Stop(); 
    Task StartPublishingAsync(string serviceName, string deviceName); 
    Task StartSubscribingAsync(string serviceName); 
    Task StartPublisherSubscribingAsync(string serviceName, string deviceName); 
    Task ConnectToPeerAsync(WifiDirectPeer peer); 
    Task AcceptInvitationAsync(string token); 
    Task RejectInvitationAsync(string token); 
} 
