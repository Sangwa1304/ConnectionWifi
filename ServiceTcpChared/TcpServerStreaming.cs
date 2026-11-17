using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ServiceTcpChared;

public class TcpServerStreaming : StreamingData, ITcpStreaming
{
    private TcpListener? Server;
    private TcpClient? Client;

    public int Port { get; }

    public IPAddress Address { get; }

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
        Stop();

        TcpListener server = new(Address, Port);

        Server = server;

        try
        {
            server.Start(1);

            Debug.WriteLine(
                $"Serveur TCP en écoute sur le port {Port}");

            // Attente d'une connexion entrante.
            TcpClient client =
                await server.AcceptTcpClientAsync(token)
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
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Erreur du serveur TCP : {ex}");

            ConnectionClosed?.Invoke();
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

    public void Add(List<string> items)
    {
        if (items == null || items.Count == 0)
        {
            return;
        }

        _ = FilesToSend.AddAsync(items.ToArray());
    }

    public void Add(string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return;
        }

        _ = FilesToSend.AddAsync([item]);
    }
}