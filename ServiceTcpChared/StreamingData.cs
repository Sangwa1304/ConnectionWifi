
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
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

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
    public float Percentage
        {
        get
        {
            if (_Counter <= 0)
            {
                return 0;
            }

            return Math.Clamp((_Counter - Counter) * 100f / _Counter, 0, 100);
        }
    }
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
    public bool IsWorking => IsSending || IsReceving;
    public bool IsReceving => ReceveidTask is { IsCompleted: false };
    public bool IsSending => SendTask is { IsCompleted: false };
    private Task? SendTask;
    private Task? ReceveidTask;
    private CancellationTokenSource? SendCancellation;
    private CancellationTokenSource? ReceiveCancellation;
    protected NetworkStream? Stream;
    protected readonly AsyncCollection<string[]> FilesToSend = new();
    protected const int BufferSize = 2 * 1024 * 1024;

    public StreamingData()
    {
        if(!Directory.Exists(DownloadDirectory))
        {
            _= Directory.CreateDirectory(DownloadDirectory);
        }
    }

    public virtual event Action? StreamingCreated;
    public virtual event Action? ConnectionClosed;

    public async Task StartSend(CancellationToken token, string[]? files = null)
    {
        if (files is not null)
        {
            await FilesToSend.AddAsync(files);
        }

        if (IsSending) return;

        SendCancellation?.Dispose();
        SendCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken operationToken = SendCancellation.Token;
        SendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var files in FilesToSend.GetAsyncCollection(operationToken))
                {
                    if (!await ReceiveResponseAsync(operationToken)) return;
                    Dictionary<InfoMeta, string> pathlist = InfoMeta.GetMetas(files);
                    string metaArgsForSend = InfoMeta.GetJsonMetasTabel(InfoMeta.GetInfoMetaArgs(pathlist));
                    try
                    {
                        await SendFrameAsync(Encoding.UTF8.GetBytes(metaArgsForSend), operationToken);
                        await Task.Delay(1000, operationToken);
                        if (!await ReceiveResponseAsync(operationToken)) continue;
                        while (pathlist.Count != 0)
                        {
                            InfoMeta? requestedMeta = null;
                            var requestedMetastring = await ReceivedMetaEchnatillonFromClientAsync(token: operationToken);
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
                            byte[] bytes = new byte[BufferSize];
                            int i;

                            while ((i = await file.ReadAsync(bytes, operationToken)) != 0)
                            {
                                await GetStream().WriteAsync(bytes.AsMemory(0, i), operationToken);
                                Counter -= i;
                            }
                            FilesFiniched.Add(new(requestedMeta.NameWithExtension, requestedMeta.Length, TransfertType.Envoi, DateTime.Now));
                            _Counter = 0;
                            pathlist.Remove(requestedMeta);
                        }
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Erreur d'envoi : {ex.Message}");
                        ConnectionClosed?.Invoke();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erreur d'envoi : {ex.Message}");
                ConnectionClosed?.Invoke();
            }
        }, operationToken);
    }

    public async Task StartReceived(CancellationToken token)
    {
        if (IsReceving) return;

        ReceiveCancellation?.Dispose();
        ReceiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken operationToken = ReceiveCancellation.Token;
        ReceveidTask = Task.Run(async () =>
        {
            try
            {
                while (!operationToken.IsCancellationRequested)
                {
                    await SendResponseAsync(true, operationToken);
                    await Task.Delay(100, operationToken);

                    List<InfoMeta> infoMetaArgsReceived = [.. await ReceivedAllsMetaFromServer(operationToken)];

                    if (FilesComming?.Invoke(infoMetaArgsReceived) ?? false)
                    {
                        await SendResponseAsync(false, operationToken);
                        await Task.Delay(2000, operationToken);
                        infoMetaArgsReceived.Clear();
                    }
                    else
                    {
                        await SendResponseAsync(true, operationToken);
                        await Task.Delay(2000, operationToken);
                    }

                    try
                    {
                        while (infoMetaArgsReceived.Count != 0 && !operationToken.IsCancellationRequested)
                        {
                            var metaFile = infoMetaArgsReceived.First();
                            await Task.Delay(100, operationToken);


                            string temp = metaFile.GetTempFile(DownloadDirectory, ".san");
                            string direct = metaFile.GetDirectFile(DownloadDirectory);
                            FileReceivedOrSend = metaFile.NameWithExtension;
                            // Côté client : envoyer la métadonnée au serveur
                            await SendMetaEchantillonToServerAsync(metaFile, operationToken);

                            if (!await ReceiveResponseAsync(operationToken))
                            {
                                infoMetaArgsReceived.Remove(metaFile);
                                continue;
                            }

                            // Côté client : recevoir le fichier et l'écrire
                            using (var stm = File.Create(temp))
                            {
                                await foreach (var f in StartReadBytesAsync(null, metaFile.Length, operationToken))
                                {
                                    await stm.WriteAsync(f, token);
                                }
                            }
                            FilesFiniched.Add(new(metaFile.NameWithExtension, metaFile.Length, TransfertType.Recu, DateTime.Now));
                            infoMetaArgsReceived.Remove(metaFile);
                            File.Move(temp, direct);
                        }
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch(Exception ex)
                    {
                        Debug.WriteLine("Erreur de reception : " + ex.Message);
                        ConnectionClosed?.Invoke();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
            }
            catch(Exception ex)
            {
                Debug.WriteLine("Erreur de reception : " + ex.Message);
                ConnectionClosed?.Invoke();
            }
        }, operationToken);
    }

    private async Task<InfoMeta[]> ReceivedAllsMetaFromServer(CancellationToken token)
    {
        var nb = await ReceivedMetaEchnatillonFromClientAsync(true, token);
        return InfoMeta.GetObjectMetasTabel(nb).Meta;
    }

    public virtual void Stop()
    {
        SendCancellation?.Cancel();
        ReceiveCancellation?.Cancel();
        Stream?.Dispose();
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StartReadBytesAsync(Action fileComplete, long count, [EnumeratorCancellation] CancellationToken token)
    {
        _Counter = Counter = count;
        byte[] bytes = new byte[BufferSize];
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
                fileComplete?.Invoke();
                break;
            }
        }
    }

    private async Task SendMetaEchantillonToServerAsync(InfoMeta metaFile, CancellationToken token)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(metaFile.Json);
        await SendFrameAsync(jsonBytes, token);
    }

    private async Task<bool> ReceiveResponseAsync(CancellationToken token)
    {
        byte[] response = await ReceiveFrameAsync(token);
        return JsonConvert.DeserializeObject<bool>(Encoding.UTF8.GetString(response));
    }

    private async Task SendResponseAsync(bool response,CancellationToken token)
    {
        var data = JsonConvert.SerializeObject(response);
        await SendFrameAsync(Encoding.UTF8.GetBytes(data), token);
    }

    private async Task<string> ReceivedMetaEchnatillonFromClientAsync(bool bufferForAllMetas = true,CancellationToken token = default)
    {
        // si c'est le serveur, on attend la métadonnée du fichier à envoyer, sinon on attend la métadonnée du fichier à recevoir
        
        byte[] frame = await ReceiveFrameAsync(token);
        return Encoding.UTF8.GetString(frame);
    }

    private NetworkStream GetStream() => Stream ?? throw new InvalidOperationException("Aucun flux TCP actif.");

    private async Task SendFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("La trame TCP est trop volumineuse.");
        }

        NetworkStream stream = GetStream();
        byte[] lengthPrefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthPrefix, token);
        await stream.WriteAsync(payload, token);
    }

    private async Task<byte[]> ReceiveFrameAsync(CancellationToken token)
    {
        NetworkStream stream = GetStream();
        byte[] lengthPrefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthPrefix, token);

        int payloadLength = BitConverter.ToInt32(lengthPrefix, 0);
        if (payloadLength < 0 || payloadLength > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("La taille de la trame TCP est invalide.");
        }

        byte[] payload = new byte[payloadLength];
        await ReadExactlyAsync(stream, payload, token);
        return payload;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0)
            {
                throw new EndOfStreamException("La connexion TCP a été fermée par le pair.");
            }

            offset += read;
        }
    }
}
