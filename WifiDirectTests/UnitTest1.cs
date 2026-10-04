using WindowsWifiLibrary.Services;

namespace WifiDirectTests;

public class UnitTest1
{
    [Fact]
    public async Task WifiDirectTestStatus()
    {
        WindowsWifiDirectService service = new();

        Assert.True(service.CurrentStatus == WindowsWifiLibrary.Models.WifiDirectStatus.Disconnected);

        Assert.True(service.PublisherStatus == WindowsWifiLibrary.Models.WifiDirectPublicherStatus.Closed);

        await service.StartPublishingAsync();
        Assert.True(service.PublisherStatus == WindowsWifiLibrary.Models.WifiDirectPublicherStatus.Started);


        Assert.True(service.WatcherStatus == WindowsWifiLibrary.Models.WifiDirectWatcherStatus.Closed);

        await service.StartSubscribingAsync();
        Assert.True(service.WatcherStatus == WindowsWifiLibrary.Models.WifiDirectWatcherStatus.Started);

        service.Stop();

        Assert.True(service.WatcherStatus == WindowsWifiLibrary.Models.WifiDirectWatcherStatus.Closed);
        Assert.True(service.PublisherStatus == WindowsWifiLibrary.Models.WifiDirectPublicherStatus.Closed);
    }
}
