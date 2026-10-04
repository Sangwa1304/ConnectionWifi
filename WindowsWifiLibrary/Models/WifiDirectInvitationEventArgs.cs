using Windows.Devices.WiFiDirect;

namespace WindowsWifiLibrary.Models;

public record WifiDirectInvitationEventArgs(string Token, WiFiDirectConnectionRequest Request);
