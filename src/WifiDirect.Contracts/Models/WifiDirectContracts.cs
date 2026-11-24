namespace WindowsWifiLibrary.Models;

public enum WifiDirectStatus { Disconnected, Connected, Aborded, Closed, NoArdwareSupported }
public enum WifiDirectWatcherStatus { Started, Closed }
public enum WifiDirectPublicherStatus { Started, Closed }

public sealed class WifiDirectPeer(string id, string name) : IEquatable<WifiDirectPeer>
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool Equals(WifiDirectPeer? other) => other is not null && Id == other.Id;
    public override bool Equals(object? obj) => Equals(obj as WifiDirectPeer);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);
    public static bool operator ==(WifiDirectPeer? left, WifiDirectPeer? right) => EqualityComparer<WifiDirectPeer>.Default.Equals(left, right);
    public static bool operator !=(WifiDirectPeer? left, WifiDirectPeer? right) => !(left == right);
}

public sealed record WifiDirectPeerEventArgs(WifiDirectPeer Peer);
public sealed record WifiDirectStatusEventArgs(WifiDirectStatus Status);
public sealed record WifiDirectWatcherStatusEventArgs(WifiDirectWatcherStatus Status);
public sealed record WifiDirectPublicherStatusEventArgs(WifiDirectPublicherStatus Status);
public sealed record WifiDirectErrorEventArgs(Exception? Error, string message);
public sealed record WifiDirectInvitationEventArgs(string Token, WifiDirectPeer Peer);
public sealed record WifiDirectConnectionEventArgs(bool ServerMode, string RemoteIpAddress, string Id);
