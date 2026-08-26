namespace WindowsWifiLibrary.Core.Models;
public class PeerDevice 
{ 
    public string DeviceId { get; set; } = Guid.NewGuid().ToString(); 
    public string DeviceName { get; set; } = string.Empty; 
    public string IpAddress { get; set; } = string.Empty; 
    public int Port { get; set; } = 5050; 
    public DeviceOperatingMode CurrentMode { get; set; } 
    public bool IsPremium { get; set; } 
}  



