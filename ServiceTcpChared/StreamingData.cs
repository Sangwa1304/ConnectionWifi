
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

using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ServiceTcpChared;

public class StreamingData
{
    public enum TransfertType
    {
        Recu,
        Envoi

    }
    public record FileFinishe(string Name, long Length, TransfertType Transfert,DateTime Time );
    public const int Port1 = 1304;
    public const int Port2 = 1904;
    public float Percentage { get
        {
            float p = _Counter - Counter;
            p *= 100;
            p /= _Counter;
            return p;
        } }
    private long _Counter { get;set; } = 0;
    public static string DownloadDirectory { get; } = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),"Shareboost");
    public event Action? CounterChange;
    public event Action? FileChange;
    private string _FileReceivedOrSend = "";
    private long _counter = 0;
    public string FileReceivedOrSend
    {
        get => _FileReceivedOrSend;
        private set
        {
            _FileReceivedOrSend = value;
            FileChange?.Invoke();
        }
    }
    public long Counter { 
        get=>_counter; 
        private set 
        {
            _counter = value;
            CounterChange?.Invoke();
        } }
    public readonly List<FileFinishe> FilesFiniched = new();
    public event Func<IEnumerable<InfoMeta>, bool> FilesComming;
    public bool IsWorking { get => IsSending && IsReceving; }
    public bool IsReceving { get=> ReceveidTask?.Status == TaskStatus.Running; }
    public bool IsSending { get => SendTask?.Status == TaskStatus.Running; } 
    private Task? SendTask;
    private Task? ReceveidTask;
    protected NetworkStream? Stream;
    public readonly AsyncCollection<string[]> FilesToSend = new();
    protected long buffer = 2048576; // 2 Mo

    public StreamingData(Action counterChange)
    {
        if(!Directory.Exists(DownloadDirectory))
        {
            _= Directory.CreateDirectory(DownloadDirectory);
        }
        CounterChange = counterChange;
    }

    public virtual event Action? StreamingCreated;
    public virtual event Action? ConnectionClosed;

    public async Task StartSend( CancellationToken token, string[]? files = null)
    {
        
        if (files != null)await FilesToSend.AddAsync(files);
        if (IsSending) return;
        token.Register(() => SendTask?.Dispose());
        SendTask?.Dispose();
        SendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var files in FilesToSend.GetAsyncCollection(token))
                {
                    if (!await ReceiveResponseAsync(token)) return;
                    Dictionary<InfoMeta, string> pathlist = InfoMeta.GetMetas(files);
                    string metaArgsForSend = InfoMeta.GetJsonMetasTabel(InfoMeta.GetInfoMetaArgs(pathlist));
                    try
                    {
                        await SendAllsMetasToClient(metaArgsForSend, token);
                        await Task.Delay(1000);
                        if (!await ReceiveResponseAsync(token)) continue;
                        while (pathlist.Count != 0)
                        {
                            InfoMeta? requestedMeta = null;
                            var requestedMetastring = await ReceivedMetaEchnatillonFromClientAsync(token: token);
                            try
                            {
                                requestedMeta = InfoMeta.Object(requestedMetastring);
                            }
                            catch (Exception ex)
                            {
                                await SendResponseAsync(false, token);
                                Console.WriteLine($"Erreur de désérialisation des métadonnées : {ex.Message}");
                                continue;
                            }

                            if (requestedMeta == null || !InfoMeta.GetInstanceDirectory(requestedMeta, pathlist, out var path))
                            {
                                await SendResponseAsync(false, token);
                                continue;

                            }
                            await SendResponseAsync(true, token);
                            await Task.Delay(1000, token);

                            using FileStream file = new(path: path, FileMode.Open, FileAccess.Read);
                            _Counter = file.Length;
                            Counter = file.Length;
                            FileReceivedOrSend = requestedMeta.NameWithExtension;
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
                            FilesFiniched.Add(new(requestedMeta.NameWithExtension, requestedMeta.Length, TransfertType.Envoi, DateTime.Now));
                            _Counter = 0;
                            pathlist.Remove(requestedMeta);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Erreur d'envoi : {ex.Message}");
                    }
                }
            }
            
            catch(Exception)
            {
                Stream?.Dispose();
                ConnectionClosed?.Invoke();
            }

        }, token);
    }

    public async Task StartReceived(CancellationToken token)
    {
        
        if (IsReceving) return;
        token.Register(() => ReceveidTask?.Dispose());

        ReceveidTask?.Dispose();
        ReceveidTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await SendResponseAsync(true, token);
                    await Task.Delay(100, token);

                    List<InfoMeta> infoMetaArgsReceived = [.. await ReceivedAllsMetaFromServer(token)];

                    if (FilesComming?.Invoke(infoMetaArgsReceived) ?? false)
                    {
                        await SendResponseAsync(false, token);
                        await Task.Delay(2000);
                    }
                    else
                    {
                        await SendResponseAsync(true, token);
                        await Task.Delay(2000);

                    }

                    try
                    {
                        while (infoMetaArgsReceived.Count != 0 && !token.IsCancellationRequested)
                        {
                            var metaFile = infoMetaArgsReceived.First();
                            await Task.Delay(100, token);


                            string temp = metaFile.GetTempFile(DownloadDirectory, ".san");
                            string direct = metaFile.GetDirectFile(DownloadDirectory);
                            FileReceivedOrSend = metaFile.NameWithExtension;
                            // Côté client : envoyer la métadonnée au serveur
                            await SendMetaEchantillonToServerAsync(metaFile);

                            if (!await ReceiveResponseAsync(token))
                            {
                                infoMetaArgsReceived.Remove(metaFile);
                                continue;
                            }

                            // Côté client : recevoir le fichier et l'écrire
                            using (var stm = File.Create(temp))
                            {
                                await foreach (var f in StartReadBytesAsync((() => { Console.WriteLine("Fichier reçu avec succès !"); }), metaFile.Length, token))
                                {
                                    await stm.WriteAsync(f, token);
                                }
                            }
                            FilesFiniched.Add(new(metaFile.NameWithExtension, metaFile.Length, TransfertType.Recu, DateTime.Now));
                            infoMetaArgsReceived.Remove(metaFile);
                            File.Move(temp, direct);
                        }
                    }
                    catch(Exception ex)
                    {
                        Debug.WriteLine("Erreur de reception : " + ex.Message);
                    }
                }
            }
            catch(Exception ex)
            {
                
                Debug.WriteLine("Erreur de reception : " + ex.Message);
                Stream?.Dispose();
                ConnectionClosed?.Invoke();
            }
        },token);
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
        var nb = await ReceivedMetaEchnatillonFromClientAsync(true, token);
        return InfoMeta.GetObjectMetasTabel(nb).Meta;
    }

    public virtual void Stop() { Stream?.Dispose(); }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StartReadBytesAsync(Action fileComplete, long count, [EnumeratorCancellation] CancellationToken token)
    {
        _Counter = Counter = count;
        byte[] bytes = new byte[buffer];
        int i;

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

    private async Task<bool> ReceiveResponseAsync(CancellationToken token)
    {
        if (Stream != null)
        {
            byte[] lengthBuffer = new byte[4];
            int bytesRead = await Stream.ReadAsync(lengthBuffer.AsMemory(0, 4), token);
            return JsonConvert.DeserializeObject<bool>(Encoding.UTF8.GetString(lengthBuffer));
        }
        return false;
    }

    private async Task SendResponseAsync(bool response,CancellationToken token)
    {
        var data = JsonConvert.SerializeObject(response);
        if (Stream != null)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(data);

            // Envoie d'abord la taille, puis le contenu JSON
            await Stream.WriteAsync(jsonBytes);
        }

    }

    private async Task<string> ReceivedMetaEchnatillonFromClientAsync(bool bufferForAllMetas = true,CancellationToken token = default)
    {
        // si c'est le serveur, on attend la métadonnée du fichier à envoyer, sinon on attend la métadonnée du fichier à recevoir
        
        int n = 4; // Taille du préfixe de longueur (4 octets pour un entier)

        // Si on veut recevoir toutes les métadonnées, on peut utiliser un tampon plus grand pour éviter de lire partiellement les métadonnées
        //if (bufferForAllMetas) n = 64;
        if (Stream != null)
        {
            byte[] lengthBuffer = new byte[n];
            int bytesRead = await Stream.ReadAsync(lengthBuffer.AsMemory(0, n), token);
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
