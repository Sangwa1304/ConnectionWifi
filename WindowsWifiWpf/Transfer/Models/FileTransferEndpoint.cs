using System; 
 
namespace WindowsWifiWpf.Transfer.Models; 

public class FileTransferEndpoint 
{ 
    public Uri Uri { get; } 
    public string? SessionToken { get; } 

    public FileTransferEndpoint(Uri uri, string? sessionToken = null) 
    { 
        Uri = uri ?? throw new ArgumentNullException(nameof(uri)); 
        SessionToken = sessionToken; 
    } 
} 

public class FileTransferProgress 
{ 
    public long BytesTransferred { get; set; } 
    public long TotalBytes { get; set; } 
    public double Percentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 
100.0 : 0; 
} 