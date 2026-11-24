using Android.Content;
using Android.Net.Wifi.P2p;
using Android.OS;
using Android.Runtime;
using Microsoft.Maui.ApplicationModel;

namespace ShareBoost.Droid.Platforms.Android;

/// <summary>
/// Android Wi-Fi Direct implementation. Android's system UI owns consent for
/// incoming P2P negotiations; this service never treats a discovery result as
/// an accepted connection.
/// </summary>
public sealed class AndroidWifiDirectService : IDisposable
{
    private readonly Context _context;
    private readonly WifiP2pManager _manager;
    private readonly WifiP2pManager.Channel _channel;
    private readonly P2pReceiver _receiver;
    private readonly object _sync = new();
    private readonly Dictionary<string, WifiP2pDevice> _peers = new();
    private TaskCompletionSource<AndroidWifiDirectConnection>? _connectionRequest;
    private bool _receiverRegistered;
    private bool _disposed;

    public AndroidWifiDirectStatus Status { get; private set; } = AndroidWifiDirectStatus.Closed;
    public IReadOnlyCollection<AndroidWifiDirectPeer> Peers
    {
        get
        {
            lock (_sync)
                return _peers.Values.Select(ToPeer).ToArray();
        }
    }

    public event EventHandler<AndroidWifiDirectStatus>? StatusChanged;
    public event EventHandler<AndroidWifiDirectPeer>? PeerDiscovered;
    public event EventHandler<AndroidWifiDirectPeer>? PeerUpdated;
    public event EventHandler<AndroidWifiDirectPeer>? PeerRemoved;
    public event EventHandler<AndroidWifiDirectConnection>? ConnectionEstablished;
    public event EventHandler<string>? Error;

    public AndroidWifiDirectService()
    {
        _context = Platform.AppContext;
        _manager = (WifiP2pManager?)_context.GetSystemService(Context.WifiP2pService)
            ?? throw new NotSupportedException("Le service Wi-Fi Direct n'est pas disponible sur cet appareil.");

        _channel = _manager.Initialize(_context, Looper.MainLooper, null)
            ?? throw new InvalidOperationException("Impossible d'initialiser le canal Wi-Fi Direct.");

        _receiver = new P2pReceiver(this);
    }

    public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!_receiverRegistered)
        {
            var filter = new IntentFilter();
            filter.AddAction(WifiP2pManager.WifiP2pStateChangedAction);
            filter.AddAction(WifiP2pManager.WifiP2pPeersChangedAction);
            filter.AddAction(WifiP2pManager.WifiP2pConnectionChangedAction);
            filter.AddAction(WifiP2pManager.WifiP2pThisDeviceChangedAction);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                _context.RegisterReceiver(_receiver, filter, ReceiverFlags.NotExported);
            else
#pragma warning disable CA1422
                _context.RegisterReceiver(_receiver, filter);
#pragma warning restore CA1422
            _receiverRegistered = true;
        }

        SetStatus(AndroidWifiDirectStatus.Disconnected);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Makes this device available as a group owner. This does not accept a
    /// remote peer by itself; Android controls incoming negotiation consent.
    /// </summary>
    public Task<bool> StartPublishingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _manager.CreateGroup(_channel, new ActionListener(
            () => completion.TrySetResult(true),
            reason =>
            {
                ReportError($"Impossible de publier le service Wi-Fi Direct : {reason}.");
                completion.TrySetResult(false);
            }));

        return completion.Task.WaitAsync(cancellationToken);
    }

    public Task<bool> StartSubscribingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _manager.DiscoverPeers(_channel, new ActionListener(
            () => completion.TrySetResult(true),
            reason =>
            {
                ReportError($"Échec de la découverte Wi-Fi Direct : {reason}.");
                completion.TrySetResult(false);
            }));

        return completion.Task.WaitAsync(cancellationToken);
    }

    public async Task<bool> StartPublisherSubscribingAsync(CancellationToken cancellationToken = default)
    {
        var published = await StartPublishingAsync(cancellationToken).ConfigureAwait(false);
        if (!published)
            return false;

        return await StartSubscribingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a connection and waits for Android to report an actually
    /// formed P2P group. The remote device/system can still reject the request.
    /// </summary>
    public async Task<AndroidWifiDirectConnection?> ConnectToPeerAsync(
        AndroidWifiDirectPeer peer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(peer);

        WifiP2pDevice device;
        lock (_sync)
        {
            if (!_peers.TryGetValue(peer.Address, out device!))
                throw new InvalidOperationException("Cet appareil n'est plus dans la liste des appareils découverts.");

            if (_connectionRequest is not null)
                throw new InvalidOperationException("Une négociation Wi-Fi Direct est déjà en cours.");

            _connectionRequest = new TaskCompletionSource<AndroidWifiDirectConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        SetStatus(AndroidWifiDirectStatus.Connecting);

        try
        {
            var config = new WifiP2pConfig
            {
                DeviceAddress = device.DeviceAddress,
                Wps = new WpsInfo { Setup = WpsInfo.Pbc }
            };

            var request = _connectionRequest;
            _manager.Connect(_channel, config, new ActionListener(
                () => { /* OnSuccess means request submitted, not connected. */ },
                reason =>
                {
                    request.TrySetException(new InvalidOperationException(
                        $"La demande de connexion a été rejetée : {reason}."));
                }));

            using var registration = cancellationToken.Register(() =>
                request.TrySetCanceled(cancellationToken));

            // A negotiation can be left pending by the OS or the other device.
            var connection = await request.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken)
                .ConfigureAwait(false);

            return connection;
        }
        catch (TimeoutException)
        {
            ReportError("Délai dépassé : aucune connexion Wi-Fi Direct n'a été confirmée.");
            SetStatus(AndroidWifiDirectStatus.Disconnected);
            return null;
        }
        catch (OperationCanceledException)
        {
            _manager.CancelConnect(_channel, new ActionListener(() => { }, _ => { }));
            SetStatus(AndroidWifiDirectStatus.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            SetStatus(AndroidWifiDirectStatus.Disconnected);
            return null;
        }
        finally
        {
            lock (_sync)
                _connectionRequest = null;
        }
    }

    public Task DisconnectAsync()
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.RemoveGroup(_channel, new ActionListener(
            () => completion.TrySetResult(true),
            _ => completion.TrySetResult(false)));
        return completion.Task;
    }

    private void RefreshPeers()
    {
        _manager.RequestPeers(_channel, new PeerListListener(devices =>
        {
            var current = devices.DeviceList.ToDictionary(d => d.DeviceAddress, d => d);
            List<AndroidWifiDirectPeer> added;
            List<AndroidWifiDirectPeer> updated;
            List<AndroidWifiDirectPeer> removed;

            lock (_sync)
            {
                added = current.Values.Where(d => !_peers.ContainsKey(d.DeviceAddress)).Select(ToPeer).ToList();
                updated = current.Values.Where(d => _peers.ContainsKey(d.DeviceAddress)).Select(ToPeer).ToList();
                removed = _peers.Values.Where(d => !current.ContainsKey(d.DeviceAddress)).Select(ToPeer).ToList();
                _peers.Clear();
                foreach (var item in current)
                    _peers[item.Key] = item.Value;
            }

            foreach (var peer in added) PeerDiscovered?.Invoke(this, peer);
            foreach (var peer in updated) PeerUpdated?.Invoke(this, peer);
            foreach (var peer in removed) PeerRemoved?.Invoke(this, peer);
        }));
    }

    private void RefreshConnection()
    {
        _manager.RequestConnectionInfo(_channel, new ConnectionInfoListener(info =>
        {
            if (!info.GroupFormed)
                return;

            // Android exposes the group-owner address, not necessarily the
            // remote client's address when this device is the group owner.
            var address = info.GroupOwnerAddress?.HostAddress ?? string.Empty;
            var connection = new AndroidWifiDirectConnection(
                info.IsGroupOwner,
                address,
                info.GroupFormed);

            SetStatus(AndroidWifiDirectStatus.Connected);
            lock (_sync)
                _connectionRequest?.TrySetResult(connection);

            ConnectionEstablished?.Invoke(this, connection);
        }));
    }

    private void OnBroadcast(Intent? intent)
    {
        if (intent?.Action == WifiP2pManager.WifiP2pPeersChangedAction)
            RefreshPeers();
        else if (intent?.Action == WifiP2pManager.WifiP2pConnectionChangedAction)
            RefreshConnection();
        else if (intent?.Action == WifiP2pManager.WifiP2pStateChangedAction)
        {
            var enabled = intent.GetIntExtra(WifiP2pManager.ExtraWifiState, -1)
                == (int)WifiP2pState.Enabled;
            if (!enabled)
            {
                SetStatus(AndroidWifiDirectStatus.WifiDisabled);
                lock (_sync)
                    _connectionRequest?.TrySetException(
                        new InvalidOperationException("Le Wi-Fi Direct a été désactivé."));
            }
        }
    }

    private static AndroidWifiDirectPeer ToPeer(WifiP2pDevice device) =>
        new(device.DeviceAddress, string.IsNullOrWhiteSpace(device.DeviceName)
            ? "Appareil sans nom"
            : device.DeviceName, device.Status);

    private void SetStatus(AndroidWifiDirectStatus status)
    {
        if (Status == status)
            return;
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void ReportError(string message) => Error?.Invoke(this, message);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (_sync)
        {
            _connectionRequest?.TrySetCanceled();
            _connectionRequest = null;
            _peers.Clear();
        }

        if (_receiverRegistered)
        {
            _context.UnregisterReceiver(_receiver);
            _receiverRegistered = false;
        }

        _manager.StopPeerDiscovery(_channel, new ActionListener(() => { }, _ => { }));
        _manager.RemoveGroup(_channel, new ActionListener(() => { }, _ => { }));
        SetStatus(AndroidWifiDirectStatus.Closed);
    }

    private sealed class ActionListener(Action success, Action<WifiP2pFailureReason> failure)
        : Java.Lang.Object, WifiP2pManager.IActionListener
    {
        public void OnSuccess() => success();
        public void OnFailure([GeneratedEnum] WifiP2pFailureReason reason) => failure(reason);
    }

    private sealed class PeerListListener(Action<WifiP2pDeviceList> callback)
        : Java.Lang.Object, WifiP2pManager.IPeerListListener
    {
        public void OnPeersAvailable(WifiP2pDeviceList peers) => callback(peers);
    }

    private sealed class ConnectionInfoListener(Action<WifiP2pInfo> callback)
        : Java.Lang.Object, WifiP2pManager.IConnectionInfoListener
    {
        public void OnConnectionInfoAvailable(WifiP2pInfo info) => callback(info);
    }

    private sealed class P2pReceiver(AndroidWifiDirectService owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) => owner.OnBroadcast(intent);
    }
}

public enum AndroidWifiDirectStatus
{
    Closed,
    Disconnected,
    Connecting,
    Connected,
    WifiDisabled
}

public sealed record AndroidWifiDirectPeer(string Address, string Name, WifiP2pDeviceStatus Status);

public sealed record AndroidWifiDirectConnection(bool IsGroupOwner, string GroupOwnerAddress, bool GroupFormed);
