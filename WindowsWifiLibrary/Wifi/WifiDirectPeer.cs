using Windows.Devices.Enumeration;

namespace WindowsWifiLibrary.Wifi;

public class WifiDirectPeer
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;

    internal DeviceInformation? DiscoveredInfo { get; set; }
}

[Obsolete("Use WifiDirectPeer instead.")]
public class WifiAwarePeer : WifiDirectPeer
{
}
