namespace WindowsWifiWpf.Models;

public sealed class TransferHistoryEntry
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "Réception";
    public string Status { get; set; } = "Terminé";
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string DisplaySize => Size == 0 ? "0 o" : Size switch
    {
        < 1024 => $"{Size} o",
        < 1024 * 1024 => $"{Size / 1024.0:0.##} Ko",
        < 1024 * 1024 * 1024 => $"{Size / (1024.0 * 1024):0.##} Mo",
        _ => $"{Size / (1024.0 * 1024 * 1024):0.##} Go"
    };
}