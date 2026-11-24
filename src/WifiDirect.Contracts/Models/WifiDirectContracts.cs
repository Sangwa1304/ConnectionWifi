namespace WindowsWifiLibrary.Models;

public enum WifiDirectStatus { Disconnected, Connected, Aborded, Closed, NoArdwareSupported }
public enum WifiDirectWatcherStatus { Started, Closed }
public enum WifiDirectPublicherStatus { Started, Closed }

public sealed record WifiDirectPeer(string Id, string Name) : IEquatable<WifiDirectPeer>;
public sealed record WifiDirectPeerEventArgs(WifiDirectPeer Peer);
public sealed record WifiDirectStatusEventArgs(WifiDirectStatus Status);
public sealed record WifiDirectWatcherStatusEventArgs(WifiDirectWatcherStatus Status);
public sealed record WifiDirectPublicherStatusEventArgs(WifiDirectPublicherStatus Status);
public sealed record WifiDirectErrorEventArgs(Exception? Error, string message);
public sealed record WifiDirectInvitationEventArgs(string Token, WifiDirectPeer Peer);
public sealed record WifiDirectConnectionEventArgs(bool ServerMode, string RemoteIpAddress, string Id);
