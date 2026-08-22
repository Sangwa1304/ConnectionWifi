namespace WindowsWifiLibrary.Wifi;

public enum WifiDirectStatus
{
    None,
    HardwareNotSupported,
    Initializing,
    Ready,
    Publishing,
    Subscribing,
    Connected,
    Error
}

[Obsolete("Use WifiDirectStatus instead.")]
public enum WifiAwareStatus
{
    None = 0,
    HardwareNotSupported = 1,
    Initializing = 2,
    Ready = 3,
    Publishing = 4,
    Subscribing = 5,
    Connected = 6,
    Error = 7
}
