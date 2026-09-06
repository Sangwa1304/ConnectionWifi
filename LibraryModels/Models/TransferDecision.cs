namespace LibraryMI.Models;

public sealed class TransferDecision
{
    public bool Accepted { get; set; }
    public string? Reason { get; set; }
    public string? DestinationFolder { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
