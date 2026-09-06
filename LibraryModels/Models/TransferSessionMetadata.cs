namespace LibraryMI.Models;

public class TransferSessionMetadata
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public string SessionToken { get; set; } = string.Empty;
    public string SenderName { get; set; } = Environment.MachineName;
    public string SenderIp { get; set; } = string.Empty;
    public int SenderPort { get; set; }
    public long TotalSize { get; set; }
    public int TotalFiles { get; set; }
    public List<TransferItemMetadata> Items { get; set; } = [];
    public TransferSessionState SessionState { get; set; } = TransferSessionState.Created;
    public TransferNegotiationStatus NegotiationStatus { get; set; }
     = TransferNegotiationStatus.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? TargetFolder { get; set; }
    public string? ErrorMessage { get; set; }
}
