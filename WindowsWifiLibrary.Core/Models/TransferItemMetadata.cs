namespace WindowsWifiLibrary.Core.Models;

public class TransferItemMetadata
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullLocalPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public List<TransferItemMetadata> Children { get; set; } = [];
}
