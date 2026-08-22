namespace WindowsWifiLibrary.Wifi;

public class WifiDirectPeerEventArgs : EventArgs
{
    public WifiDirectPeer Peer { get; }

    public WifiDirectPeerEventArgs(WifiDirectPeer peer)
    {
        Peer = peer ?? throw new ArgumentNullException(nameof(peer));
    }
}

[Obsolete("Use WifiDirectPeerEventArgs instead.")]
public class WifiAwarePeerEventArgs : WifiDirectPeerEventArgs
{
    public WifiAwarePeerEventArgs(WifiAwarePeer peer)
        : base(peer)
    {
    }
}
