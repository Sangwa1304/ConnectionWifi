using Windows.Devices.WiFiDirect;
using Windows.Devices.WiFiDirect.Services;

namespace WindowsWifiLibrary.Wifi;
public class WifiAwarePeer
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string ServiceName { get; set; }
    internal WiFiDirectService DiscoveredInfo { get; set; }
}
