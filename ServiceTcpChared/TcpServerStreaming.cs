
/*
 * 
 * Protocole d'excution des trnasferts :
 * 1. L'appareil démarre et écoute les connexions entrantes (il demarre le serveur TCP et le client TCP)
 * 2. l'apareil 1 lancer le trnasfert via BLE en partageant les metadonnees, le port tcp, puis on initilise un canal de trnasmission(Wifidirect)
 * 3. l'appareil 2 se connecte à l'appareil 2 via le client TCP et l'address ip fournis via le canal de transmission ce qui arrete le serveur cote appareil 2 et demarre le serveur cote appareil 1, le client pareil.
 * 4. le client cote appareil 2 envoie la metadonnee du fichier à l'appareil 1, ce qui permet à l'appareil 1 de savoir quel fichier envoyer.
 * 5. l'appareil 1 envoie le fichier via le serveur TCP vers le client TCP de l'appareil 2.
 * 6. l'appareil 2 recoit le fichier et le sauvegarde dans le dossier de téléchargement.
 * 7. si le tableau de metadonnees est vide, le trnasfert est terminé, sinon on recommence à l'étape 4 pour un autre fichier.
 * 8. si un fichier est corrompu ou que le trnasfert est interrompu, l'appareil 2 peut renvoyer la metadonnee du fichier à l'appareil 1 pour recommencer le trnasfert du fichier.
*/

using System.Net;
using System.Net.Sockets;

namespace ServiceTcpChared;

public class TcpServerStreaming : StreamingData, ITcpStreaming
{
    private TcpListener? Server;
    private TcpClient? Client;
    public Int32 Port { get; }
    public IPAddress Address { get; }
    public override event Action? StreamingCreated;
    public override event Action? ConnectionClosed;

    public TcpServerStreaming(bool modeServer)
    {
        Port = modeServer ?  Port1: Port2;
        Address = IPAddress.Any;
    }

    public async Task StartConnection(CancellationToken token)
    {
        Stop();
        TcpListener server = new(Address, Port);
        Server = server;
        try
        {
            server.Start();
            TcpClient client = await server.AcceptTcpClientAsync(token);
            Client = client;

            Stream = client.GetStream();
            StreamingCreated?.Invoke();
            await StartSend(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            server.Stop();
        }
        catch (Exception)
        {
            server.Stop();
            ConnectionClosed?.Invoke();
        }
    }

    public override void Stop()
    {
        base.Stop();
        Server?.Stop();
        Client?.Dispose();
        Client = null;
        Server = null;
    }

    public void Add(List<string> items)
    {
        _=FilesToSend.AddAsync(items.ToArray());
    }
    public void Add(string item)
    {
        _ = FilesToSend.AddAsync([item]);
    }
}
