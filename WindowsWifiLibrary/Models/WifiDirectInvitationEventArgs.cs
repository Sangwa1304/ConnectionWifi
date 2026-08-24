using Windows.Devices.WiFiDirect;

namespace WindowsWifiLibrary.Models;

public class WifiDirectInvitationEventArgs : EventArgs  
{  
    public string Token { get; set; } = string.Empty;  
    public WiFiDirectConnectionRequest Request { get; set; } = null!; 
} 
