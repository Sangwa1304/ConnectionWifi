using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ServiceTcpChared;

public class TcpServerStreaming : StreamingData, ITcpStreaming
{
    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(45);
    private TcpListener? Server;
    private TcpClient? Client;

    public int Port { get; }

    public IPAddress Address { get; }

    /// <summary>Remote endpoint of the accepted TCP client, available after StreamingCreated.</summary>
    public string? RemoteIpAddress => (Client?.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();

    public override event Action? StreamingCreated;
    public override event Action? ConnectionClosed;

    public TcpServerStreaming(bool modeServer)
        : base()
    {
        // Le serveur écoute sur le port correspondant à son rôle.
        Port = modeServer ? Port1 : Port2;

        Address = IPAddress.Any;
    }

    public async Task StartConnection(CancellationToken token)
    {
        TcpListener server = new(Address, Port);

        Server = server;

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(AcceptTimeout);

        try
        {
            server.Start(1);

            Debug.WriteLine(
                $"Serveur TCP en écoute sur le port {Port}");

            // Attente d'une connexion entrante.
            TcpClient client =
                await server.AcceptTcpClientAsync(timeout.Token)
                    .ConfigureAwait(false);

            Client = client;

            Client.NoDelay = true;
            Client.ReceiveBufferSize = 64 * 1024;
            Client.SendBufferSize = 64 * 1024;

            Stream = client.GetStream();

            Debug.WriteLine(
                $"Client TCP accepté sur le port {Port}");

            StreamingCreated?.Invoke();

            // Le serveur devient l'émetteur sur ce canal.
            await StartSend(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            Debug.WriteLine("Serveur TCP arrêté.");
        }
        catch (SocketException)
            when (token.IsCancellationRequested)
        {
            Debug.WriteLine("Écoute TCP interrompue.");
        }
        catch (OperationCanceledException ex)
            when (!token.IsCancellationRequested)
        {
            TimeoutException timeoutException = new(
                $"Aucun canal TCP entrant n'a été établi sur le port {Port} après {AcceptTimeout.TotalSeconds:0} secondes.",
                ex);
            ConnectionClosed?.Invoke();
            throw timeoutException;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Erreur du serveur TCP : {ex}");

            ConnectionClosed?.Invoke();
            throw;
        }
        finally
        {
            Stop();
        }
    }

    public override void Stop()
    {
        base.Stop();

        try
        {
            Server?.Stop();
        }
        catch (SocketException)
        {
        }

        Client?.Dispose();

        Server = null;
        Client = null;
    }

    public Task<bool> EnqueueAsync(IEnumerable<string> items, CancellationToken token)
    {
        return QueueSendAsync(items, token);
    }
}
