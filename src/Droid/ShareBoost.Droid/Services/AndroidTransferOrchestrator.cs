using System.Net;
using System.Runtime.ExceptionServices;
using LibraryMI.Models;
using ServiceTcpChared;
using WindowsWifiLibrary.Models;

namespace ShareBoost.Droid.Services;

/// <summary>
/// Runs the same paired TCP server/client channels, ports, framing and role
/// ordering as the Windows MainViewModel. When Android is group owner, the
/// peer IP is learned from the accepted TCP socket instead of guessing a DHCP
/// address from Wi-Fi Direct metadata.
/// </summary>
public sealed class AndroidTransferOrchestrator : IDisposable
{
    public TcpClientStreaming? ClientInstance { get; private set; }
    public TcpServerStreaming? ServerInstance { get; private set; }
    public event Func<InfoMetas, Task<bool>>? IncomingTransferRequested;
    public event Action? ProgressChanged;
    public event Action? FilesChanged;
    public event Action<StreamingData.FileFinishe>? FileTransferCompleted;

    private Task? _serverTask;
    private Task? _clientTask;
    private CancellationTokenSource? _sessionCancellation;
    private TaskCompletionSource<bool>? _ready;
    private TaskCompletionSource<string>? _acceptedPeerIp;
    private int _readyChannels;
    private volatile bool _isRunning;
    private bool _disposed;

    public bool IsRunning => _isRunning && _ready?.Task.IsCompletedSuccessfully == true;
    public float SendProgress => ServerInstance?.Percentage ?? 0;
    public float ReceiveProgress => ClientInstance?.Percentage ?? 0;
    public string SendDescription => ServerInstance?.FileReceivedOrSend ?? string.Empty;
    public string ReceiveDescription => ClientInstance?.FileReceivedOrSend ?? string.Empty;

    public Task WaitUntilReadyAsync(CancellationToken token) =>
        (_ready?.Task ?? Task.FromException(new InvalidOperationException("La session TCP n'a pas été démarrée."))).WaitAsync(token);

    public async Task StartAsync(WifiDirectConnectionEventArgs connection, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(connection);

        Stop();
        _sessionCancellation?.Dispose();
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sessionToken = _sessionCancellation.Token;
        _readyChannels = 0;
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ServerInstance = new TcpServerStreaming(connection.ServerMode);
        ServerInstance.StreamingCreated += OnServerChannelReady;
        ServerInstance.FileChange += OnFilesChanged;
        ServerInstance.CounterChange += OnProgressChanged;
        ServerInstance.FilesComming += OnIncomingFiles;
        ServerInstance.FileTransferCompleted += OnFileTransferCompleted;
        _serverTask = ServerInstance.StartConnection(sessionToken);
        _isRunning = true;

        string remoteAddress = connection.RemoteIpAddress;
        if (string.IsNullOrWhiteSpace(remoteAddress))
        {
            // If Android is the group owner, WifiP2pInfo does not expose the
            // client's IP. Windows' client channel connects to this server;
            // its accepted socket supplies the authoritative remote endpoint.
            _acceptedPeerIp = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task endpointTask = _acceptedPeerIp.Task;
            Task firstServerEvent = await Task.WhenAny(endpointTask, _serverTask).ConfigureAwait(false);
            if (firstServerEvent == _serverTask)
                await _serverTask.ConfigureAwait(false);
            remoteAddress = await _acceptedPeerIp.Task.WaitAsync(TimeSpan.FromSeconds(50), sessionToken).ConfigureAwait(false);
        }

        if (!IPAddress.TryParse(remoteAddress, out _))
            throw new InvalidOperationException("Le pair TCP n'a pas fourni une adresse IP distante valide.");

        ClientInstance = new TcpClientStreaming(remoteAddress, connection.ServerMode);
        ClientInstance.StreamingCreated += OnClientChannelReady;
        ClientInstance.FileChange += OnFilesChanged;
        ClientInstance.CounterChange += OnProgressChanged;
        ClientInstance.FilesComming += OnIncomingFiles;
        ClientInstance.FileTransferCompleted += OnFileTransferCompleted;

        if (connection.ServerMode)
            _clientTask = ClientInstance.StartConnection(sessionToken);
        else
            _clientTask = ClientInstance.StartConnection(sessionToken);

        Task serverTask = _serverTask;
        Task clientTask = _clientTask;
        Task first = await Task.WhenAny(serverTask, clientTask).ConfigureAwait(false);
        Exception? failure = null;
        try { await first.ConfigureAwait(false); }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; _ready?.TrySetException(ex); }
        finally
        {
            _sessionCancellation.Cancel();
            ServerInstance?.Stop();
            ClientInstance?.Stop();
        }

        try { await Task.WhenAll(serverTask, clientTask).ConfigureAwait(false); }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
        catch (Exception ex) { failure ??= ex; _ready?.TrySetException(ex); }

        _isRunning = false;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public Task<bool> SendFilesAsync(IEnumerable<FileI> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ServerInstance is null || !IsRunning)
            throw new InvalidOperationException("Les canaux TCP ne sont pas prêts. Reconnectez l'appareil.");
        return ServerInstance.EnqueueAsync(files.Select(f => f.PathFile).ToList(), token);
    }

    private Task<bool> OnIncomingFiles(IEnumerable<InfoMeta> files) =>
        IncomingTransferRequested?.Invoke(new InfoMetas(files)) ?? Task.FromResult(false);

    private void OnServerChannelReady()
    {
        if (_acceptedPeerIp is not null)
        {
            var address = ServerInstance?.RemoteIpAddress;
            if (!string.IsNullOrWhiteSpace(address))
                _acceptedPeerIp.TrySetResult(address);
        }
        MarkChannelReady();
    }

    private void OnClientChannelReady() => MarkChannelReady();

    private void MarkChannelReady()
    {
        if (Interlocked.Increment(ref _readyChannels) == 2)
            _ready?.TrySetResult(true);
    }

    private void OnProgressChanged() => ProgressChanged?.Invoke();
    private void OnFilesChanged() => FilesChanged?.Invoke();
    private void OnFileTransferCompleted(StreamingData.FileFinishe file) => FileTransferCompleted?.Invoke(file);

    public void Stop()
    {
        _isRunning = false;
        _sessionCancellation?.Cancel();
        _acceptedPeerIp?.TrySetCanceled();
        ServerInstance?.Stop();
        ClientInstance?.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
    }
}
