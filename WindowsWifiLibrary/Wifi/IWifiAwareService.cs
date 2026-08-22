namespace WindowsWifiLibrary.Wifi;

public interface IWifiAwareService : IDisposable
{
    WifiAwareStatus CurrentStatus { get; }
    bool IsHardwareSupported { get; }
    event EventHandler<WifiAwareStatus> StatusChanged;
    event EventHandler<WifiAwarePeerEventArgs> PeerDiscovered, PeerUpdated, PeerRemoved;
    event EventHandler<WifiAwareInvitationEventArgs> InvitationReceived;
    event EventHandler<WifiAwareConnectionEventArgs> ConnectionEstablished;
    Task<bool> InitializeAsync();
    void Stop();
    Task StartPublishingAsync(string sName, string dName);
    Task StartSubscribingAsync(string sName);
    Task ConnectToPeerAsync(WifiAwarePeer peer);
    Task AcceptInvitationAsync(string token);
    Task RejectInvitationAsync(string token);
}