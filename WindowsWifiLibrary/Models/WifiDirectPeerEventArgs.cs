
namespace WindowsWifiLibrary.Models;

public class WifiDirectPeerEventArgs : EventArgs  
{  
    public WifiDirectPeer Peer { get; set; } = new();  
} 
