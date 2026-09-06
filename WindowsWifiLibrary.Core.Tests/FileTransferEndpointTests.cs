using WindowsWifiLibrary.Core.Models;
using Xunit;

namespace WindowsWifiLibrary.Core.Tests;

public class FileTransferEndpointTests
{
    [Fact]
    public void Constructor_stores_uri_and_session_token()
    {
        var uri = new Uri("http://192.168.1.10:8080/");

        var endpoint = new FileTransferEndpoint(uri, "session-token");

        Assert.Same(uri, endpoint.Uri);
        Assert.Equal("session-token", endpoint.SessionToken);
    }

    [Fact]
    public void Constructor_rejects_null_uri()
    {
        Assert.Throws<ArgumentNullException>(() => new FileTransferEndpoint(null!));
    }
}
