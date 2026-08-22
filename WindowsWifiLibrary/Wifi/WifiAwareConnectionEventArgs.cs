using Windows.Devices.WiFiDirect.Services;

namespace WindowsWifiLibrary.Wifi;

public class WifiAwareConnectionEventArgs : EventArgs
{
    public string SessionId { get; set; }
    public string LocalIPv4 { get; set; }
    public string RemoteIPv4 { get; set; }
    public string LocalIPv6 { get; set; } // IPv6 Link-Local
    public string RemoteIPv6 { get; set; } // IPv6 Link-Local
    public WiFiDirectServiceSession RawSession { get; set; } //le terme WiFiDirectServiceSession n'existe pas
}