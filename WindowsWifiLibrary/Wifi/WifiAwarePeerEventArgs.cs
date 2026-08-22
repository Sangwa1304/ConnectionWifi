namespace WindowsWifiLibrary.Wifi;

public class WifiAwarePeerEventArgs : EventArgs
{
    public WifiAwarePeer Peer { get; }
    public WifiAwarePeerEventArgs(WifiAwarePeer peer) => Peer = peer;
}
