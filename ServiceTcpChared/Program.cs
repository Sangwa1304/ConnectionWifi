
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
    private static TcpClientStreaming client1;
    private static TcpClientStreaming client2;
    private static TcpServerStreaming server1;
    private static TcpServerStreaming server2;

    public static string DownloadDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public async static Task Main(string[] args)
    {
        client1 = new TcpClientStreaming(IPAddress.Loopback, 14000, Stopserver1(), null);
        client2 = new TcpClientStreaming(IPAddress.Loopback, 15000, Stopserver2(), null);
        server1 = new TcpServerStreaming(IPAddress.Any, 15000, Stopclient1(), null);
        server2 = new TcpServerStreaming(IPAddress.Any, 14000, Stopclient2(), null);

        _ = server1.StartConnection(default);
        await client2.StartConnection(default);

        string[] files =
            {
            "C:\\Users\\Glory San\\Documents\\CodeWorkspace\\Code_Csharp\\Documentation\\dotnet-api-dotnet-api-docs-temp-_splitted-system-net-10.0.pdf",
            "C:\\Users\\Glory San\\Documents\\CodeWorkspace\\Code_Csharp\\Documentation\\aspnet-core-aspnetcore-10.0.pdf"
            
            };

        // Démarrage du serveur pour l'écoute des demandes
        _ = server1.StartSend(files, default);

        // Boucle côté client pour réclamer et recevoir chaque fichier
        _= client2.StartReceived(DownloadDirectory,default);
        await Task.Delay(15000);
        await server1.FilesToSend.AddAsync(["C:\\Users\\Glory San\\Documents\\CodeWorkspace\\Code_Csharp\\Documentation\\uwp-api-docs-conceptual-toc-winrt.pdf"]);
        while (true) ;
    }
    private static Action Stopserver1() => () => server1?.Stop();
    private static Action Stopserver2() => () => server2?.Stop();
    private static Action Stopclient1() => () => client1?.Stop();
    private static Action Stopclient2() => () => client2?.Stop();
}
