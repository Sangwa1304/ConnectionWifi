namespace WindowsWifiWpf.Transfer;

public sealed record FileTransferProgress(long BytesTransferred, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : BytesTransferred * 100d / TotalBytes;
}
