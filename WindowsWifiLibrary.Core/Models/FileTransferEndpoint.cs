namespace WindowsWifiLibrary.Core.Models;

public class FileTransferEndpoint(Uri uri, string? sessionToken = null)
{
    public Uri Uri { get; } = uri ?? throw new ArgumentNullException(nameof(uri));
    public string? SessionToken { get; } = sessionToken;
}