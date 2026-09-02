namespace TansfertApiCore;

public sealed class TransferAcceptanceRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string? DestinationFolder { get; set; }
}
