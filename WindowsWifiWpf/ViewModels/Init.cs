using LibraryMI.Interfaces;
using TcpApiTransfert;
using WindowsWifiLibrary.Core.Services;

namespace WindowsWifiWpf.ViewModels;

public static class Init
{
    public static IFileTransferService GetService()
    {
        return new FileTransferService();
    }
}
