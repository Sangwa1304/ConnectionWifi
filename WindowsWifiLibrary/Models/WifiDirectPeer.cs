
namespace WindowsWifiLibrary.Models;

public class WifiDirectPeer  
{  
    public string Id { get; set; } = string.Empty; 
    public string Name { get; set; } = string.Empty;
    public override string ToString() => Name;
} 
