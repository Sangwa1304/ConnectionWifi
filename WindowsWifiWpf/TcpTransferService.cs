namespace WindowsWifiWpf;

public class TcpTransferService(IPAddress localaddr, int port) : TcpListener(localaddr, port)
{
    public async Task StartAsync(string storage)
    {
        throw new NotImplementedException();
    }
}
