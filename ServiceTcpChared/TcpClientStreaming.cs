using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ServiceTcpChared;

public class TcpClientStreaming : StreamingData, ITcpStreaming
{
    private TcpClient? Client;

    private static readonly TimeSpan ConnectionTimeout =
        TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RetryDelay =
        TimeSpan.FromMilliseconds(500);

    public int Port { get; }

    public IPAddress Address { get; }

    public override event Action? StreamingCreated;
    public override event Action? ConnectionClosed;

    public TcpClientStreaming(string address, bool serverMode)
        : base()
    {
        if (!IPAddress.TryParse(address, out IPAddress? ipAddress))
        {
            throw new ArgumentException(
                "L'adresse IP distante est invalide.",
                nameof(address));
        }

        Address = ipAddress;

        // Le client se connecte au port du serveur distant.
        Port = serverMode ? Port2 : Port1;
    }

    public async Task StartConnection(CancellationToken token)
    {
        Stop();

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(token);

        timeout.CancelAfter(ConnectionTimeout);

        TcpClient? candidate = null;

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                candidate = new TcpClient(Address.AddressFamily)
                {
                    NoDelay = true,
                    ReceiveBufferSize = 64 * 1024,
                    SendBufferSize = 64 * 1024
                };

                try
                {
                    await candidate.ConnectAsync(
                        Address,
                        Port,
                        timeout.Token
                    ).ConfigureAwait(false);

                    Client = candidate;
                    Stream = candidate.GetStream();

                    StreamingCreated?.Invoke();

                    Debug.WriteLine(
                        $"Connexion TCP établie vers {Address}:{Port}");

                    await StartReceived(token).ConfigureAwait(false);

                    return;
                }
                catch (SocketException ex)
                    when (!timeout.IsCancellationRequested)
                {
                    Debug.WriteLine(
                        $"Connexion TCP impossible : {ex.Message}");

                    candidate.Dispose();
                    candidate = null;

                    await Task.Delay(
                        RetryDelay,
                        timeout.Token
                    ).ConfigureAwait(false);
                }
            }

            throw new TimeoutException(
                $"Impossible de joindre {Address}:{Port} après 30 secondes.");
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            Debug.WriteLine("Connexion TCP annulée.");
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine("Délai de connexion TCP dépassé.");

            ConnectionClosed?.Invoke();

            throw new TimeoutException(
                $"Délai dépassé pour la connexion à {Address}:{Port}.",
                ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Erreur du client TCP : {ex}");

            ConnectionClosed?.Invoke();
        }
        finally
        {
            candidate?.Dispose();

            Stop();
        }
    }

    public override void Stop()
    {
        base.Stop();

        Client?.Dispose();
        Client = null;
    }
}