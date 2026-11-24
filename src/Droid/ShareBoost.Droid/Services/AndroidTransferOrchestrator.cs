using System.Runtime.ExceptionServices;
using LibraryMI.Models;
using ServiceTcpChared;
using WindowsWifiLibrary.Models;

namespace ShareBoost.Droid.Services;

public sealed class AndroidTransferOrchestrator : IDisposable
{
    public TcpClientStreaming? ClientInstance { get; private set; }
    public TcpServerStreaming? ServerInstance { get; private set; }
    public event Func<InfoMetas, Task<bool>>? IncomingTransferRequested;
    public event Action? ProgressChanged;
    public event Action? FilesChanged;
    private Task? _serverTask;
    private Task? _clientTask;
    private CancellationTokenSource? _sessionCancellation;
    private TaskCompletionSource<bool>? _ready;
    private int _readyChannels;
    private volatile bool _isRunning;
    private bool _disposed;

    public bool IsRunning => _isRunning;
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
        if (string.IsNullOrWhiteSpace(connection.RemoteIpAddress))
            throw new ArgumentException("L'adresse IP distante n'a pas été résolue.", nameof(connection));

        Stop();
        _sessionCancellation?.Dispose();
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sessionToken = _sessionCancellation.Token;
        _readyChannels = 0;
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ServerInstance = new TcpServerStreaming(connection.ServerMode);
        ClientInstance = new TcpClientStreaming(connection.RemoteIpAddress, connection.ServerMode);
        ServerInstance.StreamingCreated += OnChannelReady;
        ClientInstance.StreamingCreated += OnChannelReady;
        ServerInstance.FileChange += OnFilesChanged;
        ClientInstance.FileChange += OnFilesChanged;
        ServerInstance.CounterChange += OnProgressChanged;
        ClientInstance.CounterChange += OnProgressChanged;
        ServerInstance.FilesComming += OnIncomingFiles;
        ClientInstance.FilesComming += OnIncomingFiles;

        if (connection.ServerMode)
        {
            _serverTask = ServerInstance.StartConnection(sessionToken);
            _clientTask = ClientInstance.StartConnection(sessionToken);
        }
        else
        {
            _clientTask = ClientInstance.StartConnection(sessionToken);
            _serverTask = ServerInstance.StartConnection(sessionToken);
        }

        _isRunning = true;
        Task serverTask = _serverTask;
        Task clientTask = _clientTask;
        Task first = await Task.WhenAny(serverTask, clientTask).ConfigureAwait(false);
        Exception? failure = null;
        try { await first.ConfigureAwait(false); }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; _ready.TrySetException(ex); }
        finally
        {
            _sessionCancellation.Cancel();
            ServerInstance?.Stop();
            ClientInstance?.Stop();
        }

        try { await Task.WhenAll(serverTask, clientTask).ConfigureAwait(false); }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
        catch (Exception ex) { failure ??= ex; _ready.TrySetException(ex); }

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

    private void OnChannelReady()
    {
        if (Interlocked.Increment(ref _readyChannels) == 2)
            _ready?.TrySetResult(true);
    }

    private void OnProgressChanged() => ProgressChanged?.Invoke();
    private void OnFilesChanged() => FilesChanged?.Invoke();

    public void Stop()
    {
        _isRunning = false;
        _sessionCancellation?.Cancel();
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
