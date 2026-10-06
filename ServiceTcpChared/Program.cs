
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

namespace ServiceTcpChared;

internal class Program
{
    private static CancellationTokenSource _ctsTcp;
    private static TcpClientStreaming ClientInstance;
    private static TcpServerStreaming ServerInstance;

    
    public async static Task Main(string[] args)
    {
        string[] files =
            {
            "C:\\Users\\Glory San\\Documents\\CodeWorkspace\\Code_Csharp\\Documentation\\dotnet-api-dotnet-api-docs-temp-_splitted-system-net-10.0.pdf",
            "C:\\Users\\Glory San\\Documents\\CodeWorkspace\\Code_Csharp\\Documentation\\aspnet-core-aspnetcore-10.0.pdf"
            
            };

        ServerInstance?.Stop();
        ServerInstance = new(true, CounterServerChange);
        ClientInstance?.Stop();
        ClientInstance = new(IPAddress.Loopback.ToString(), true, CounterClientChange);
        _ctsTcp?.Cancel();
        _ctsTcp = new();
        ServerInstance.FileChange += FileClientChange;
        if (true)
        {
            _ = ServerInstance.StartConnection(_ctsTcp.Token);
            _ = ClientInstance.StartConnection(_ctsTcp.Token);
        }
        else
        {
            _ = ClientInstance.StartConnection(_ctsTcp.Token);
            _ = ServerInstance.StartConnection(_ctsTcp.Token);
        }

        await ServerInstance.FilesToSend.AddAsync(files);
        while (true) ;
        
    }

    private static void CounterClientChange()
    {
        Console.WriteLine("Client " + ClientInstance.Percentage.ToString());
    }
    
    private static void FileClientChange()
    {
        Console.WriteLine("Client " + ClientInstance.FileReceivedOrSend);
    }
    private static void FileServerChange()
    {
        Console.WriteLine("Server " + ServerInstance.FileReceivedOrSend);
    }
    private static void CounterServerChange()
    {
        Console.WriteLine("Server " + ServerInstance.Percentage.ToString());
    }
}
