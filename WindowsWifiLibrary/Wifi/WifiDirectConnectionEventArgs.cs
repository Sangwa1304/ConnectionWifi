namespace WindowsWifiLibrary.Wifi;

public class WifiDirectConnectionEventArgs : EventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public string LocalIPv4 { get; set; } = string.Empty;
    public string RemoteIPv4 { get; set; } = string.Empty;
    public string LocalIPv6 { get; set; } = string.Empty;
    public string RemoteIPv6 { get; set; } = string.Empty;

    public object? RawSession { get; set; }
}

[Obsolete("Use WifiDirectConnectionEventArgs instead.")]
public class WifiAwareConnectionEventArgs : WifiDirectConnectionEventArgs
{
}
