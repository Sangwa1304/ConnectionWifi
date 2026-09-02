namespace TansfertApiCore;

public sealed class TransferChunkedFileResponse
{
    public required string SessionId { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public required long ContentLength { get; init; }
    public required string ContentType { get; init; } = "application/octet-stream";
}
