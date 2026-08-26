namespace WindowsWifiLibrary.Core.Models;

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