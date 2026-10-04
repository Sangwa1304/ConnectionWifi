
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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;

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
        client1 = new TcpClientStreaming(IPAddress.Loopback, 14000, stopserver1(), null);
        client2 = new TcpClientStreaming(IPAddress.Loopback, 15000, stopserver2(), null);
        server1 = new TcpServerStreaming(IPAddress.Any, 15000, stopclient1(), null);
        server2 = new TcpServerStreaming(IPAddress.Any, 14000, stopclient2(), null);

        _ = server1.StartConnection(default);
        await client2.StartConnection(default);

        string[] files =
            {
            "C:\\Users\\Glory San\\Documents\\CodeWorkspace\\Code_Csharp\\Documentation\\dotnet-api-dotnet-api-docs-temp-_splitted-system-net-10.0.pdf"
            };


        // Démarrage du serveur pour l'écoute des demandes
        _ = server1.StartSend(files, default);

        // Boucle côté client pour réclamer et recevoir chaque fichier
        await client2.StartReceived(DownloadDirectory,default);
    }
    private static Action stopserver1() => () => server1?.Stop();
    private static Action stopserver2() => () => server2?.Stop();
    private static Action stopclient1() => () => client1?.Stop();
    private static Action stopclient2() => () => client2?.Stop();
}

public class InfoMeta : IEquatable<InfoMeta?>
{
    public bool Etat = false;
    public readonly string NameWithExtension;
    public readonly long Length;

    // Correction : Utilisation sécurisée de Path.GetFileNameWithoutExtension
    private string NameWithoutExtension => Path.GetFileNameWithoutExtension(NameWithExtension);
    public record InfoMetaArgs(InfoMeta[] Meta);

    [JsonIgnore]
    public string Json => JsonConvert.SerializeObject(new
    {
        NameWithExtension,
        Length
    });

    public static InfoMeta? Object(string json)
    {
        try
        {
            var metadata = JsonConvert.DeserializeObject<MetadataPayload>(json);

            if (metadata is null || string.IsNullOrWhiteSpace(metadata.NameWithExtension))
            {
                return null;
            }

            return new InfoMeta(metadata.NameWithExtension, metadata.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur de désérialisation des métadonnées : {ex.Message}");
            return null;
        }
    }

    private sealed class MetadataPayload
    {
        public string? NameWithExtension { get; set; }
        public long Length { get; set; }
    }

    public InfoMeta(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            throw new ArgumentException("Le chemin du fichier est obligatoire.", nameof(file));
        }

        var inf = new FileInfo(file);
        if (!inf.Exists)
        {
            throw new FileNotFoundException("Le fichier source est introuvable.", file);
        }

        Length = inf.Length;
        NameWithExtension = inf.Name;
    }

    private InfoMeta(string nameWithExtension, long length)
    {
        NameWithExtension = nameWithExtension;
        Length = length;
    }
    public static InfoMetaArgs GetInfoMetaArgs(Dictionary<InfoMeta, string> metasDictionnarys)
    {
        InfoMeta[] metas = new InfoMeta[metasDictionnarys.Count];
        metasDictionnarys.Keys.CopyTo(metas, 0);
        return new InfoMetaArgs(metas);
    }

    public static string GetJsonMetasTabel(InfoMetaArgs metaArgs)
    {
        List<MetadataPayload> infoMetas = new();

        foreach(var inf in metaArgs.Meta)
        {
            infoMetas.Add(new MetadataPayload { NameWithExtension = inf.NameWithoutExtension, Length = inf.Length });
        }

        return JsonConvert.SerializeObject(infoMetas);
    }

    public static InfoMetaArgs GetObjectMetasTabel(string metaArgs)
    {
        var liste = JsonConvert.DeserializeObject<List<MetadataPayload>>(metaArgs);
        List<InfoMeta> infoMetas = new();
        if (liste != null)
        {
            foreach(var inf in liste)
            {
                infoMetas.Add(new(inf.NameWithExtension, inf.Length));
            }
        }

        return new([.. infoMetas]);
    }

    public string GetDirectFile(string downloadPath)
    {
        var direct = Path.Combine(downloadPath, NameWithExtension);
        if (File.Exists(direct))
        {
           return ReadapteFileName(direct);
        }
        else
        {
            return direct;
        }  
    }

    private string ReadapteFileName( string direct)
    {
        if(File.Exists(direct))
        {
            var n = Path.GetFileName(direct);
            if (n.StartsWith("Again_("))
            {
                _ = int.TryParse(n.Split(['(', ')'], 3)[2], out int h);
                
                h++;
                try
                {
                    n.Replace(n.Split(['(', ')'], 3)[2], h.ToString()); 
                }
                catch(Exception)
                {
                    n = "s_" + n;
                }
                
            }
            else n = "s_" + n;

            string direc = direct.Replace(Path.GetFileName(direct), n);
            return ReadapteFileName(direc);
        }
        else
        {
            return direct;
        }
    }

    public string GetTempFile(string downloadPath, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith("."))
        {
            extension = ".san";
        }
        return Path.Combine(downloadPath, $"{NameWithoutExtension}{extension}");
    }

    internal static Dictionary<InfoMeta, string> GetMetas(string[] files)
    {
        var pathlist = new Dictionary<InfoMeta, string>();
        foreach (var file in files)
        {
            try
            {
                var meta = new InfoMeta(file);
                pathlist[meta] = file;
            }
            catch (Exception)
            {
                continue;
            }
        }
        return pathlist;
    }

    public override bool Equals(object? obj) => Equals(obj as InfoMeta);

    public bool Equals(InfoMeta? other)
    {
        return other is not null &&
               NameWithExtension == other.NameWithExtension &&
               Length == other.Length;
    }

    public override int GetHashCode() => HashCode.Combine(NameWithExtension, Length);

    public static bool operator ==(InfoMeta? left, InfoMeta? right) => EqualityComparer<InfoMeta>.Default.Equals(left, right);
    public static bool operator !=(InfoMeta? left, InfoMeta? right) => !(left == right);
}

public class StreamingData
{
    public long Counter { get; private set; } = 0;
    public readonly BlockingCollection<byte[]> BytesSend = new();
    protected NetworkStream? Stream;
    protected long buffer = 2048576; // 2 Mo
    public virtual event Action? StreamingStarted;
    public virtual event Action? ConnectionClosed;

    public async Task StartSend(string[] files, CancellationToken token)
    {

        Dictionary<InfoMeta, string> pathlist = InfoMeta.GetMetas(files);
        string metaArgsForSend = InfoMeta.GetJsonMetasTabel(InfoMeta.GetInfoMetaArgs(pathlist));
        
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {

                    await SendAllsMetasToClient(metaArgsForSend, token);
                    await Task.Delay(1000);

                    InfoMeta? requestedMeta = null;
                    var requestedMetastring = await ReceivedMetaEchnatillonFromClientAsync(token: token);
                    try
                    {
                            requestedMeta = InfoMeta.Object(requestedMetastring);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Erreur de désérialisation des métadonnées : {ex.Message}");
                        continue;
                    }
                    if (requestedMeta == null || !pathlist.TryGetValue(requestedMeta, out var path))
                    {
                        break;
                    }

                    using FileStream file = new(path: path, FileMode.Open, FileAccess.Read);
                    Counter = file.Length;

                    byte[] bytes = new byte[buffer];
                    int i;

                    while ((i = await file.ReadAsync(bytes, token)) != 0)
                    {
                        if (Stream != null)
                        {
                            await Stream.WriteAsync(bytes.AsMemory(0, i), token);
                        }

                        Counter -= i;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erreur d'envoi : {ex.Message}");
            }
        }, token);
    }

    private async Task SendAllsMetasToClient(string metaArgsForSend, CancellationToken token)
    {
        if (Stream != null)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(metaArgsForSend);
            byte[] lengthPrefix = BitConverter.GetBytes(metaArgsForSend.Length); // 4 octets pour la taille

            // Envoie d'abord la taille, puis le contenu JSON
            await Stream.WriteAsync(lengthPrefix);
            await Stream.WriteAsync(jsonBytes);
        }
    }

    private async Task<InfoMeta[]> ReceivedAllsMetaFromServer(CancellationToken token)
    {
        return InfoMeta.GetObjectMetasTabel(await ReceivedMetaEchnatillonFromClientAsync(true, token)).Meta;
    }

    public async Task StartReceived(string downloadDirectory,CancellationToken token)
    {
        InfoMeta[] infoMetaArgsReceived = await ReceivedAllsMetaFromServer(token);

        foreach (var metaFile in infoMetaArgsReceived)
        {
            // Côté client : envoyer la métadonnée au serveur
            await SendMetaEchantillonToServerAsync(metaFile);

            string temp = metaFile.GetTempFile(downloadDirectory, ".san");
            string direct = metaFile.GetDirectFile(downloadDirectory);

            // Côté client : recevoir le fichier et l'écrire
            using (var stm = File.Create(temp))
            {
                long state = 0;
                await foreach (var f in StartReadBytesAsync(() => Console.WriteLine("Fichier reçu avec succès !"), token, metaFile.Length))
                {
                    state += f.Length;
                    await stm.WriteAsync(f);
                }
            }

            File.Move(temp, direct);
        }
    }

    public virtual void Stop() { }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StartReadBytesAsync(Action fileComplete, [EnumeratorCancellation] CancellationToken token, long count)
    {
        Counter = count;
        byte[] bytes = new byte[buffer];
        int i;

        token.ThrowIfCancellationRequested();
        if(Stream == null)
        {
            throw new InvalidOperationException("No active stream available");
        }
        while (Counter > 0 && (i = await Stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, Counter)), token)) != 0)
        {
            yield return bytes.AsMemory(0, i);

            Counter -= i;

            if (Counter <= 0)
            {
                fileComplete();
                break;
            }
        }
    }

    private async Task SendMetaEchantillonToServerAsync(InfoMeta metaFile)
    {
        if (Stream != null)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(metaFile.Json);
            byte[] lengthPrefix = BitConverter.GetBytes(jsonBytes.Length); // 4 octets pour la taille

            // Envoie d'abord la taille, puis le contenu JSON
            await Stream.WriteAsync(lengthPrefix);
            await Stream.WriteAsync(jsonBytes);
        }
    }

    private async Task<string> ReceivedMetaEchnatillonFromClientAsync(bool bufferForAllMetas = true,CancellationToken token = default)
    {
        // si c'est le serveur, on attend la métadonnée du fichier à envoyer, sinon on attend la métadonnée du fichier à recevoir
        
        int n = 4; // Taille du préfixe de longueur (4 octets pour un entier)

        // Si on veut recevoir toutes les métadonnées, on peut utiliser un tampon plus grand pour éviter de lire partiellement les métadonnées
        if (bufferForAllMetas) n = 64;
        if (Stream != null)
        {
            byte[] lengthBuffer = new byte[n];
            int bytesRead = await Stream.ReadAsync(lengthBuffer, 0, n, token);
            if (bytesRead < n) return ""; // Connexion fermée

            int jsonLength = BitConverter.ToInt32(lengthBuffer, 0);
            byte[] jsonBuffer = new byte[jsonLength];

            int totalRead = 0;
            while (totalRead < jsonLength)
            {
                int read = await Stream.ReadAsync(jsonBuffer, totalRead, jsonLength - totalRead, token);
                if (read == 0) break;
                totalRead += read;
            }

            return Encoding.UTF8.GetString(jsonBuffer, 0, totalRead);
            
        }
        throw new InvalidOperationException("No active stream available");
    }
}

public interface ITcpStreaming
{
    Int32 Port { get; }
    IPAddress Address { get; }
    event Action? ConnectionClosed;
    event Action? StreamingStarted;

    Task StartConnection(CancellationToken token);
    void Stop();
}

public class TcpClientStreaming : StreamingData, ITcpStreaming
{
    TcpClient? Client;
    public int Port { get; }
    public IPAddress Address { get; }

    public override event Action? StreamingStarted;
    public override event Action? ConnectionClosed;

    public TcpClientStreaming(IPAddress address, int port, Action? streamReady, Action? connectionClosed)
    {
        Port = port;
        StreamingStarted += streamReady;
        ConnectionClosed += connectionClosed;
        Address = address;
    }

    public async Task StartConnection(CancellationToken token)
    {
        Client = new();
        try
        {
            await Task.Delay(100, token);
            await Client.ConnectAsync(Address, Port, token);

            Stream = Client.GetStream();
            StreamingStarted?.Invoke();
        }
        catch (Exception)
        {
            Client?.Close();
        }
    }

    public override void Stop()
    {
        Client?.Close();
    }
}

public class TcpServerStreaming : StreamingData, ITcpStreaming
{
    private TcpListener? Server;
    public Int32 Port { get; }
    public IPAddress Address { get; }
    public override event Action? StreamingStarted;
    public override event Action? ConnectionClosed;

    public TcpServerStreaming(IPAddress address, int port, Action? streamReady, Action? connectionClosed)
    {
        Port = port;
        StreamingStarted += streamReady;
        ConnectionClosed += connectionClosed;
        Address = address;
    }

    public async Task StartConnection(CancellationToken token)
    {
        Server = new(Address, Port);
        Server.Start();
        try
        {
            await Task.Delay(100, token);
            var client = await Server.AcceptTcpClientAsync(token);

            Stream = client.GetStream();
            StreamingStarted?.Invoke();
        }
        catch (Exception)
        {
            Server?.Stop();
        }
    }

    public override void Stop()
    {
        Server?.Stop();
    }
}
