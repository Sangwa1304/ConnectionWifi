namespace WindowsWifiLibrary.Wifi;

public class WifiDirectInvitationEventArgs : EventArgs
{
    public string Token { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
}

[Obsolete("Use WifiDirectInvitationEventArgs instead.")]
public class WifiAwareInvitationEventArgs : WifiDirectInvitationEventArgs
{
}


