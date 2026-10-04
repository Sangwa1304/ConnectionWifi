
namespace WindowsWifiLibrary.Models;

public class WifiDirectPeer(string Id, string Name) : IEquatable<WifiDirectPeer?>
{
    public string Id { get; } = Id;
    public string Name { get; } = Name;

    public override bool Equals(object? obj)
    {
        return Equals(obj as WifiDirectPeer);
    }

    public bool Equals(WifiDirectPeer? other)
    {
        return other is not null &&
               Id == other.Id;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id);
    }

    public static bool operator ==(WifiDirectPeer? left, WifiDirectPeer? right)
    {
        return EqualityComparer<WifiDirectPeer>.Default.Equals(left, right);
    }

    public static bool operator !=(WifiDirectPeer? left, WifiDirectPeer? right)
    {
        return !(left == right);
    }
}