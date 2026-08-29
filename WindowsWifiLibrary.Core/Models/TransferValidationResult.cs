namespace WindowsWifiLibrary.Core.Models;

public sealed class TransferValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> NormalizedPaths { get; } = [];
}
