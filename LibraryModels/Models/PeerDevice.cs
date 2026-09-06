namespace LibraryMI.Models;

public class PeerDevice
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;
    public string Status { get; set; } = "Available";
}
