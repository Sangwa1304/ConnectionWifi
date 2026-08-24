namespace WindowsWifiLibrary.Models; 
 
public class WifiDirectConnectionEventArgs : EventArgs  
{  
    public string LocalIpAddress { get; set; } = string.Empty;  
    public string RemoteIpAddress { get; set; } = string.Empty;  
} 
