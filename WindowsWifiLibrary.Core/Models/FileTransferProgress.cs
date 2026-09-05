namespace WindowsWifiLibrary.Core.Models;

public class FileTransferProgress
{
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double Percentage => TotalBytes == 0 ? 0 : (double)BytesTransferred / TotalBytes * 100;
    public string CurrentFileName { get; set; } = string.Empty;
}
