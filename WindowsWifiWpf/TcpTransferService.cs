using System.Net;
using System.Net.Sockets;

namespace WindowsWifiWpf;

public class TcpTransferService:TcpListener
{
    public TcpTransferService(IPAddress localaddr, int port) : base(localaddr, port)
    {
    }

    public async Task StartAsync(string storage)
    {
        throw new NotImplementedException();
    }
}
