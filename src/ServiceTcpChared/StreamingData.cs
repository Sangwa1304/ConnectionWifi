using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace ServiceTcpChared;

public class StreamingData
{
    public enum TransfertType
    {
        Recu,
        Envoi
    }

    public record FileFinishe(
        string Name,
        long Length,
        TransfertType Transfert,
        DateTime Time
    );

    public const int Port1 = 1304;
    public const int Port2 = 1904;

    private const int BufferSize = 64 * 1024;
    private const int MaxFrameSize = 16 * 1024 * 1024;
    private static readonly TimeSpan PayloadIdleTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan FrameTimeout =
        TimeSpan.FromSeconds(30);

    private readonly string SessionId =
        Guid.NewGuid().ToString("N");

    private readonly object _sendLock = new();
    private readonly object _receiveLock = new();
    private readonly object _lifecycleLock = new();
    private bool _stopped;

    private Task? _sendTask;
    private Task? _receiveTask;

    private CancellationTokenSource? _sendCancellation;
    private CancellationTokenSource? _receiveCancellation;

    protected NetworkStream? Stream;

    private sealed record PendingSend(
        string[] Files,
        CancellationToken CancellationToken,
        TaskCompletionSource<bool> Completion);

    private readonly AsyncCollection<PendingSend> FilesToSend = new();
    private readonly ConcurrentDictionary<TaskCompletionSource<bool>, byte> _pendingSendCompletions = new();

    private long _counter;
    private long _totalCounter;

    private string _fileReceivedOrSend = string.Empty;

    public static string DownloadDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            "Downloads",
            "Shareboost");

    public event Action? CounterChange;
    public event Action? FileChange;

    public virtual event Action? StreamingCreated;
    public virtual event Action? ConnectionClosed;

    public event Func<IEnumerable<InfoMeta>, Task<bool>>? FilesComming;

    public readonly List<FileFinishe> FilesFiniched = new();

    public bool IsWorking => IsSending || IsReceving;

    public bool IsSending =>
        _sendTask is { IsCompleted: false };

    public bool IsReceving =>
        _receiveTask is { IsCompleted: false };

    public long Counter
    {
        get => Interlocked.Read(ref _counter);

        private set
        {
            Interlocked.Exchange(ref _counter, value);
            CounterChange?.Invoke();
        }
    }

    public string FileReceivedOrSend
    {
        get => _fileReceivedOrSend;

        private set
        {
            _fileReceivedOrSend = value;
            FileChange?.Invoke();
        }
    }

    public float Percentage
    {
        get
        {
            long total = Interlocked.Read(ref _totalCounter);
            long remaining = Counter;

            if (total <= 0)
            {
                return 0;
            }

            return Math.Clamp(
                (total - remaining) * 100f / total,
                0,
                100);
        }
    }

    public StreamingData()
    {
        Directory.CreateDirectory(DownloadDirectory);
    }

    // ============================================================
    // ENVOI
    // ============================================================

    public async Task StartSend(
        CancellationToken token,
        string[]? files = null)
    {
        if (files is { Length: > 0 })
        {
            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingSend pending = new(
                files,
                token,
                completion);
            await EnqueuePendingSendAsync(pending).ConfigureAwait(false);
        }

        Task task;

        lock (_sendLock)
        {
            if (_sendTask is { IsCompleted: false })
            {
                return;
            }

            _sendCancellation?.Dispose();

            _sendCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(token);

            _sendTask = SendLoopAsync(_sendCancellation.Token);

            task = _sendTask;
        }

        await task.ConfigureAwait(false);
    }

    public async Task<bool> QueueSendAsync(IEnumerable<string> files, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(files);
        token.ThrowIfCancellationRequested();

        string[] snapshot = files.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("Au moins un fichier est requis.", nameof(files));
        }

        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await EnqueuePendingSendAsync(new PendingSend(snapshot, token, completion)).ConfigureAwait(false);

            using CancellationTokenRegistration registration = token.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                completion);

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingSendCompletions.TryRemove(completion, out _);
        }
    }

    private Task EnqueuePendingSendAsync(PendingSend pending)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _pendingSendCompletions.TryAdd(pending.Completion, 0);
            try
            {
                return FilesToSend.AddAsync(pending);
            }
            catch
            {
                _pendingSendCompletions.TryRemove(pending.Completion, out _);
                throw;
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        PendingSend? current = null;
        try
        {
            await foreach (
                PendingSend pending in FilesToSend.GetAsyncCollection(token)
                    .ConfigureAwait(false))
            {
                current = pending;
                token.ThrowIfCancellationRequested();

                if (pending.CancellationToken.IsCancellationRequested)
                {
                    pending.Completion.TrySetCanceled();
                    current = null;
                    continue;
                }

                using CancellationTokenSource batchCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(token, pending.CancellationToken);
                bool succeeded = await SendBatchAsync(pending.Files, batchCancellation.Token).ConfigureAwait(false);
                pending.Completion.TrySetResult(succeeded);
                _pendingSendCompletions.TryRemove(pending.Completion, out _);
                current = null;
            }
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            current?.Completion.TrySetCanceled();
            if (current is not null)
            {
                _pendingSendCompletions.TryRemove(current.Completion, out _);
            }
            foreach (PendingSend pending in FilesToSend.GetCollection())
            {
                pending.Completion.TrySetCanceled();
                _pendingSendCompletions.TryRemove(pending.Completion, out _);
            }

            Debug.WriteLine("Envoi TCP annulé.");
        }
        catch (Exception ex)
        {
            current?.Completion.TrySetException(ex);
            if (current is not null)
            {
                _pendingSendCompletions.TryRemove(current.Completion, out _);
            }
            foreach (PendingSend pending in FilesToSend.GetCollection())
            {
                pending.Completion.TrySetException(ex);
                _pendingSendCompletions.TryRemove(pending.Completion, out _);
            }

            Debug.WriteLine($"Erreur d'envoi TCP : {ex}");

            ConnectionClosed?.Invoke();

            throw;
        }
    }

    private async Task<bool> SendBatchAsync(string[] files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // Le destinataire annonce qu'il est prêt.
        bool receiverReady =
            await ReceiveResponseAsync(token).ConfigureAwait(false);

        if (!receiverReady)
        {
            return false;
        }

        Dictionary<InfoMeta, string> pathList =
            InfoMeta.GetMetas(files, SessionId);

        if (pathList.Count == 0)
        {
            throw new InvalidDataException("Aucun fichier valide à envoyer.");
        }

        if (pathList.Count > InfoMeta.MaxFilesPerTransfer)
        {
            throw new InvalidDataException("Le nombre de fichiers dépasse la limite autorisée.");
        }

        long totalSize = 0;

        foreach (string path in pathList.Values)
        {
            totalSize = checked(totalSize + new FileInfo(path).Length);
        }

        if (totalSize > InfoMeta.MaxFileLength)
        {
            throw new InvalidDataException("La taille totale du transfert dépasse la limite autorisée.");
        }

        // Transmission des métadonnées.
        string metadataJson =
            InfoMeta.GetJsonMetasTabel(InfoMeta.GetInfoMetaArgs(pathList));

        await SendFrameAsync(Encoding.UTF8.GetBytes(metadataJson), token).ConfigureAwait(false);

        // Attente de l'acceptation par le destinataire.
        bool accepted = await ReceiveResponseAsync(token).ConfigureAwait(false);

        if (!accepted)
        {
            Debug.WriteLine("Transfert refusé par le destinataire.");
            return false;
        }

        // Transmission individuelle des fichiers.
        while (pathList.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            string requestedMetadata =
                await ReceiveMetadataAsync(token).ConfigureAwait(false);

            InfoMeta? requestedMeta = InfoMeta.Object(requestedMetadata);

            if (requestedMeta == null ||
                !InfoMeta.GetInstanceDirectory(requestedMeta, pathList, out string path))
            {
                await SendResponseAsync(false, token).ConfigureAwait(false);
                throw new InvalidDataException("Le pair a demandé un fichier qui n'appartient pas à la session active.");
            }

            FileInfo sourceInfo = new(path);

            if (!sourceInfo.Exists || sourceInfo.Length != requestedMeta.Length)
            {
                await SendResponseAsync(false, token).ConfigureAwait(false);
                throw new IOException($"Le fichier source a changé ou n'est plus disponible : {requestedMeta.NameWithExtension}");
            }

            await SendResponseAsync(true, token).ConfigureAwait(false);

            await SendFilePayloadAsync(path, requestedMeta, token).ConfigureAwait(false);

            FilesFiniched.Add(new FileFinishe(
                requestedMeta.NameWithExtension,
                requestedMeta.Length,
                TransfertType.Envoi,
                DateTime.Now));

            pathList.Remove(requestedMeta);

            Counter = 0;
            Interlocked.Exchange(ref _totalCounter, 0);

            Debug.WriteLine($"Fichier envoyé : {requestedMeta.NameWithExtension}");
        }

        return true;
    }

    private async Task SendFilePayloadAsync(
        string path,
        InfoMeta metadata,
        CancellationToken token)
    {
        await using FileStream file = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (file.Length != metadata.Length)
        {
            throw new InvalidDataException(
                "Le fichier source a changé depuis la création des métadonnées.");
        }

        FileReceivedOrSend = metadata.NameWithExtension;

        Interlocked.Exchange(ref _totalCounter, metadata.Length);
        Counter = metadata.Length;

        byte[] buffer = new byte[BufferSize];

        long remaining = metadata.Length;

        NetworkStream stream = GetStream();

        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();

            int requested = (int)Math.Min(buffer.Length, remaining);

            int read = await file.ReadAsync(
                buffer.AsMemory(0, requested),
                token).ConfigureAwait(false);

            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Le fichier source est incomplet.");
            }

            using (CancellationTokenSource inactivity =
                CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                inactivity.CancelAfter(PayloadIdleTimeout);
                try
                {
                    await stream.WriteAsync(
                        buffer.AsMemory(0, read),
                        inactivity.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("Le transfert TCP est resté inactif trop longtemps pendant l'envoi du fichier.");
                }
            }

            remaining -= read;

            Counter = remaining;
        }

        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    // ============================================================
    // RÉCEPTION
    // ============================================================

    public async Task StartReceived(CancellationToken token)
    {
        Task task;

        lock (_receiveLock)
        {
            if (_receiveTask is { IsCompleted: false })
            {
                return;
            }

            _receiveCancellation?.Dispose();

            _receiveCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(token);

            _receiveTask =
                ReceiveLoopAsync(_receiveCancellation.Token);

            task = _receiveTask;
        }

        await task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Le destinataire annonce qu'il est prêt.
                await SendResponseAsync(
                    true,
                    token).ConfigureAwait(false);

                // Réception de la liste des fichiers proposés.
                string metadataJson =
                    await ReceiveMetadataAsync(token).ConfigureAwait(false);

                InfoMeta.InfoMetaArgs metadataArgs =
                    InfoMeta.GetObjectMetasTabel(metadataJson);

                List<InfoMeta> metadataList =
                    [.. metadataArgs.Meta];

                if (metadataList.Count == 0)
                {
                    throw new InvalidDataException(
                        "La liste des fichiers reçue est vide.");
                }

                // Demande de décision à l'interface.
                Func<IEnumerable<InfoMeta>, Task<bool>>? transferRequest = FilesComming;
                bool accepted = transferRequest is not null &&
                    await transferRequest(metadataList).ConfigureAwait(false);

                // CORRECTION IMPORTANTE :
                // true = accepté ; false = refusé.
                await SendResponseAsync(
                    accepted,
                    token).ConfigureAwait(false);

                if (!accepted)
                {
                    Debug.WriteLine("Réception refusée par l'utilisateur.");
                    continue;
                }

                Directory.CreateDirectory(DownloadDirectory);

                // Réception de chaque fichier accepté.
                foreach (InfoMeta metadata in metadataList)
                {
                    token.ThrowIfCancellationRequested();

                    await ReceiveSingleFileAsync(
                        metadata,
                        token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            Debug.WriteLine("Réception TCP annulée.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Erreur de réception TCP : {ex}");

            ConnectionClosed?.Invoke();

            throw;
        }
    }

    private async Task ReceiveSingleFileAsync(
        InfoMeta metadata,
        CancellationToken token)
    {
        string temporaryFile =
            metadata.GetTempFile(DownloadDirectory, ".part");

        string destinationFile =
            metadata.GetDirectFile(DownloadDirectory);

        try
        {
            FileReceivedOrSend = metadata.NameWithExtension;

            // Le destinataire demande ce fichier précis.
            await SendFrameAsync(
                Encoding.UTF8.GetBytes(metadata.Json),
                token).ConfigureAwait(false);

            bool fileAvailable =
                await ReceiveResponseAsync(token).ConfigureAwait(false);

            if (!fileAvailable)
            {
                Debug.WriteLine(
                    $"Fichier indisponible chez l'émetteur : {metadata.NameWithExtension}");
                throw new IOException($"Le fichier source n'est plus disponible : {metadata.NameWithExtension}");
            }

            Interlocked.Exchange(
                ref _totalCounter,
                metadata.Length);

            Counter = metadata.Length;

            // Réception des octets dans un fichier temporaire.
            await ReceiveFilePayloadAsync(
                temporaryFile,
                metadata.Length,
                token).ConfigureAwait(false);

            FileInfo receivedFile = new(temporaryFile);

            if (!receivedFile.Exists ||
                receivedFile.Length != metadata.Length)
            {
                throw new InvalidDataException(
                    "La taille du fichier reçu ne correspond pas.");
            }

            // Vérification SHA-256.
            string receivedHash =
                InfoMeta.ComputeFileHash(temporaryFile);

            if (!string.Equals(
                receivedHash,
                metadata.FileHash,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "La vérification SHA-256 du fichier a échoué.");
            }

            // Le fichier n'est déplacé qu'après validation.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryFile, destinationFile);
                    break;
                }
                catch (IOException) when (File.Exists(destinationFile) && attempt < 100_000)
                {
                    destinationFile = metadata.GetDirectFile(DownloadDirectory);
                }
            }

            FilesFiniched.Add(
                new FileFinishe(
                    metadata.NameWithExtension,
                    metadata.Length,
                    TransfertType.Recu,
                    DateTime.Now));

            Counter = 0;
            Interlocked.Exchange(ref _totalCounter, 0);

            Debug.WriteLine(
                $"Fichier reçu et vérifié : {destinationFile}");
        }
        catch
        {
            DeleteTemporaryFile(temporaryFile);
            throw;
        }
    }

    private async Task ReceiveFilePayloadAsync(
        string temporaryFile,
        long length,
        CancellationToken token)
    {
        NetworkStream stream = GetStream();

        byte[] buffer = new byte[BufferSize];

        long remaining = length;

        await using FileStream file = new(
            temporaryFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();

            int requested = (int)Math.Min(buffer.Length, remaining);

            int read;
            using (CancellationTokenSource inactivity =
                CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                inactivity.CancelAfter(PayloadIdleTimeout);
                try
                {
                    read = await stream.ReadAsync(
                        buffer.AsMemory(0, requested),
                        inactivity.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("Le transfert TCP est resté inactif trop longtemps pendant la réception du fichier.");
                }
            }

            if (read == 0)
            {
                throw new EndOfStreamException(
                    "La connexion a été interrompue pendant la réception du fichier.");
            }

            await file.WriteAsync(
                buffer.AsMemory(0, read),
                token).ConfigureAwait(false);

            remaining -= read;

            Counter = remaining;
        }

        await file.FlushAsync(token).ConfigureAwait(false);
    }

    // ============================================================
    // PROTOCOLE TCP : TRAMES ET RÉPONSES
    // ============================================================

    private async Task SendResponseAsync(
        bool response,
        CancellationToken token)
    {
        byte[] payload =
            Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(response));

        await SendFrameAsync(payload, token).ConfigureAwait(false);
    }

    private async Task<bool> ReceiveResponseAsync(
        CancellationToken token)
    {
        byte[] payload =
            await ReceiveFrameAsync(token).ConfigureAwait(false);

        bool? response =
            JsonConvert.DeserializeObject<bool>(Encoding.UTF8.GetString(payload));

        if (response == null)
        {
            throw new InvalidDataException(
                "La réponse reçue est invalide.");
        }

        return response.Value;
    }

    private async Task<string> ReceiveMetadataAsync(
        CancellationToken token)
    {
        byte[] payload =
            await ReceiveFrameAsync(token).ConfigureAwait(false);

        return Encoding.UTF8.GetString(payload);
    }

    private async Task SendFrameAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken token)
    {
        if (payload.Length > MaxFrameSize)
        {
            throw new InvalidDataException(
                "La trame TCP dépasse la taille maximale autorisée.");
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(token);

        timeout.CancelAfter(FrameTimeout);

        NetworkStream stream = GetStream();

        // Préfixe de 4 octets indiquant la taille de la trame.
        byte[] lengthPrefix =
            BitConverter.GetBytes(payload.Length);

        try
        {
            await stream.WriteAsync(lengthPrefix, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("L'envoi d'une trame TCP a dépassé le délai autorisé.");
        }
    }

    private async Task<byte[]> ReceiveFrameAsync(
        CancellationToken token)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(token);

        timeout.CancelAfter(FrameTimeout);

        NetworkStream stream = GetStream();

        byte[] lengthPrefix = new byte[sizeof(int)];

        try
        {
            await ReadExactlyAsync(stream, lengthPrefix, timeout.Token).ConfigureAwait(false);

            int payloadLength = BitConverter.ToInt32(lengthPrefix, 0);
            if (payloadLength < 0 || payloadLength > MaxFrameSize)
            {
                throw new InvalidDataException("La taille de la trame reçue est invalide.");
            }

            byte[] payload = new byte[payloadLength];
            await ReadExactlyAsync(stream, payload, timeout.Token).ConfigureAwait(false);
            return payload;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("La réception d'une trame TCP a dépassé le délai autorisé.");
        }

    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken token)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer[offset..],
                token).ConfigureAwait(false);

            if (read == 0)
            {
                throw new EndOfStreamException(
                    "La connexion TCP a été fermée par le correspondant.");
            }

            offset += read;
        }
    }

    // ============================================================
    // UTILITAIRES
    // ============================================================

    private NetworkStream GetStream()
    {
        return Stream ??
            throw new InvalidOperationException(
                "Aucun flux TCP actif.");
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Impossible de supprimer le fichier temporaire : {ex.Message}");
        }
    }

    public virtual void Stop()
    {
        try
        {
            lock (_lifecycleLock)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _sendCancellation?.Cancel();
                _receiveCancellation?.Cancel();

                foreach (TaskCompletionSource<bool> completion in _pendingSendCompletions.Keys)
                {
                    completion.TrySetCanceled();
                    _pendingSendCompletions.TryRemove(completion, out _);
                }

                Stream?.Dispose();
                Stream = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Erreur pendant l'arrêt du flux TCP : {ex.Message}");
        }
    }
}
