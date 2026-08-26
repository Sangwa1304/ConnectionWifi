namespace WindowsWifiLibrary.Core.Models;

public class TransferSessionMetadata
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public string SenderName { get; set; } = Environment.MachineName;
    public string SenderIp { get; set; } = string.Empty;
    public int SenderPort { get; set; }
    public long TotalSize { get; set; }
    public int TotalFiles { get; set; }
    public List<TransferItemMetadata> Items { get; set; } = [];
}