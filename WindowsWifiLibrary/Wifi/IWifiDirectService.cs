namespace WindowsWifiLibrary.Wifi;

public interface IWifiDirectService : IDisposable
{
    WifiDirectStatus CurrentStatus { get; }
    bool IsHardwareSupported { get; }
    event EventHandler<WifiDirectStatus>? StatusChanged;
    event EventHandler<WifiDirectPeerEventArgs>? PeerDiscovered;
    event EventHandler<WifiDirectPeerEventArgs>? PeerUpdated;
    event EventHandler<WifiDirectPeerEventArgs>? PeerRemoved;
    event EventHandler<WifiDirectInvitationEventArgs>? InvitationReceived;
    event EventHandler<WifiDirectConnectionEventArgs>? ConnectionEstablished;

    Task<bool> InitializeAsync();
    void Stop();
    Task StartPublishingAsync(string serviceName, string deviceName);
    Task StartSubscribingAsync(string serviceName);
    Task ConnectToPeerAsync(WifiDirectPeer peer);
    Task AcceptInvitationAsync(string token);
    Task RejectInvitationAsync(string token);
}

[Obsolete("Use IWifiDirectService instead.")]
public interface IWifiAwareService : IWifiDirectService
{
}
