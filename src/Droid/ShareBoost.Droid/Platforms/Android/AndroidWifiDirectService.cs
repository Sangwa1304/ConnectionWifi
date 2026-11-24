using System.Net;
using Android.Content;
using Android.Net.Wifi;
using Android.Net.Wifi.P2p;
using Android.OS;
using Android.Runtime;
using Microsoft.Maui.ApplicationModel;
using WindowsWifiLibrary.Interfaces;
using WindowsWifiLibrary.Models;

namespace ShareBoost.Droid.Platforms.Android;

/// <summary>
/// Android implementation of the exact platform-neutral Wi-Fi Direct contract
/// consumed by the Windows orchestration. Android's system UI owns incoming
/// P2P consent; this service never auto-accepts incoming invitations.
/// </summary>
public sealed class AndroidWifiDirectService : IWifiDirectService
{
    private readonly Context _context = Platform.AppContext;
    private readonly WifiP2pManager _manager;
    private readonly WifiP2pManager.Channel _channel;
    private readonly P2pReceiver _receiver;
    private readonly object _sync = new();
    private readonly Dictionary<string, WifiP2pDevice> _peers = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private TaskCompletionSource<WifiDirectConnectionEventArgs>? _connectCompletion;
    private WifiDirectPeer? _connectingPeer;
    private WifiDirectPeer? _connectedPeer;
    private bool _receiverRegistered;
    private bool _disposed;
    private WifiDirectStatus _status = WifiDirectStatus.Closed;
    private WifiDirectPublicherStatus _publisherStatus = WifiDirectPublicherStatus.Closed;
    private WifiDirectWatcherStatus _watcherStatus = WifiDirectWatcherStatus.Closed;

    public WifiDirectStatus CurrentStatus => _status;
    public WifiDirectPublicherStatus PublisherStatus => _publisherStatus;
    public WifiDirectWatcherStatus WatcherStatus => _watcherStatus;

    public event EventHandler<WifiDirectStatusEventArgs>? StatusChanged;
    public event EventHandler<WifiDirectPublicherStatusEventArgs>? PublicherStatusChanged;
    public event EventHandler<WifiDirectWatcherStatusEventArgs>? WatcherStatusChanged;
    public event EventHandler<WifiDirectPeerEventArgs>? PeerDiscovered;
    public event EventHandler<WifiDirectPeerEventArgs>? PeerUpdated;
    public event EventHandler<WifiDirectPeerEventArgs>? PeerRemoved;
    public event EventHandler<WifiDirectInvitationEventArgs>? InvitationReceived;
    public event EventHandler<WifiDirectConnectionEventArgs>? ConnectionEstablished;
    public event EventHandler<WifiDirectPeerEventArgs>? ConnectionClosed;
    public event EventHandler<WifiDirectErrorEventArgs>? Error;

    public AndroidWifiDirectService()
    {
        _manager = (WifiP2pManager?)_context.GetSystemService(Context.WifiP2pService)
            ?? throw new NotSupportedException("Le service Wi-Fi Direct n'est pas disponible sur cet appareil.");
        _channel = _manager.Initialize(_context, Looper.MainLooper, null)
            ?? throw new InvalidOperationException("Impossible d'initialiser le canal Wi-Fi Direct.");
        _receiver = new P2pReceiver(this);
    }

    public async Task<bool> InitializeAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();

        var wifiManager = (WifiManager?)_context.GetSystemService(Context.WifiService);
        if (wifiManager is null || !wifiManager.IsWifiEnabled)
        {
            SetStatus(WifiDirectStatus.Aborded);
            ReportError("Le Wi-Fi Android est désactivé. Activez-le dans les paramètres système puis réessayez.");
            return false;
        }

        PermissionStatus permission;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            permission = await Permissions.CheckStatusAsync<Permissions.NearbyWifiDevices>();
            if (permission != PermissionStatus.Granted)
                permission = await Permissions.RequestAsync<Permissions.NearbyWifiDevices>();
        }
        else
        {
#pragma warning disable CA1422
            permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (permission != PermissionStatus.Granted)
                permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
#pragma warning restore CA1422
        }

        if (permission != PermissionStatus.Granted)
        {
            SetStatus(WifiDirectStatus.NoArdwareSupported);
            ReportError("L'autorisation Android requise pour Wi-Fi Direct a été refusée.");
            return false;
        }

        if (!_receiverRegistered)
        {
            var filter = new IntentFilter();
            filter.AddAction(WifiP2pManager.WifiP2pStateChangedAction);
            filter.AddAction(WifiP2pManager.WifiP2pPeersChangedAction);
            filter.AddAction(WifiP2pManager.WifiP2pConnectionChangedAction);
            filter.AddAction(WifiP2pManager.WifiP2pThisDeviceChangedAction);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                filter.AddAction(WifiP2pManager.ActionWifiP2pRequestResponseChanged);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                _context.RegisterReceiver(_receiver, filter, ReceiverFlags.NotExported);
            else
#pragma warning disable CA1422
                _context.RegisterReceiver(_receiver, filter);
#pragma warning restore CA1422
            _receiverRegistered = true;
        }

        SetStatus(WifiDirectStatus.Disconnected);
        return true;
    }

    public Task StartPublishingAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.CreateGroup(_channel, new ActionListener(
            () =>
            {
                SetPublisherStatus(WifiDirectPublicherStatus.Started);
                completion.TrySetResult(true);
            },
            reason =>
            {
                ReportError($"Impossible de publier le service Wi-Fi Direct : {reason}.");
                completion.TrySetResult(false);
            }));
        return AwaitActionAsync(completion.Task, token);
    }

    public Task StartSubscribingAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.DiscoverPeers(_channel, new ActionListener(
            () =>
            {
                SetWatcherStatus(WifiDirectWatcherStatus.Started);
                completion.TrySetResult(true);
                RefreshPeers();
            },
            reason =>
            {
                ReportError($"Échec de la découverte Wi-Fi Direct : {reason}.");
                completion.TrySetResult(false);
            }));
        return AwaitActionAsync(completion.Task, token);
    }

    public async Task StartPublisherSubscribingAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        token.ThrowIfCancellationRequested();
        bool published = await StartPublishingAsync(token).ConfigureAwait(false);
        if (published && !token.IsCancellationRequested)
            await StartSubscribingAsync(token).ConfigureAwait(false);
    }

    public async Task<bool> ConnectToPeerAsync(WifiDirectPeer peer, CancellationToken token)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(peer);
        await _connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            WifiP2pDevice device;
            lock (_sync)
            {
                if (!_peers.TryGetValue(peer.Id, out device!))
                    throw new InvalidOperationException("Cet appareil n'est plus dans la liste des appareils découverts.");
                _connectingPeer = peer;
                _connectCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            SetStatus(WifiDirectStatus.Disconnected);
            var config = new WifiP2pConfig
            {
                DeviceAddress = device.DeviceAddress,
                Wps = new WpsInfo { Setup = WpsInfo.Pbc }
            };
            var request = _connectCompletion;
            _manager.Connect(_channel, config, new ActionListener(
                () => { /* Android accepted the request submission; group formation is awaited below. */ },
                reason => request.TrySetException(new InvalidOperationException($"La demande de connexion a échoué : {reason}."))));

            using var registration = token.Register(() => request.TrySetCanceled(token));
            var established = await request.Task.WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            SetStatus(WifiDirectStatus.Disconnected);
            ReportError("La négociation Wi-Fi Direct a expiré.");
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _manager.CancelConnect(_channel, new ActionListener(() => { }, _ => { }));
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(WifiDirectStatus.Disconnected);
            ReportError(ex.Message, ex);
            return false;
        }
        finally
        {
            lock (_sync)
            {
                _connectCompletion = null;
                _connectingPeer = null;
            }
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Android handles incoming Wi-Fi Direct negotiation through its system
    /// confirmation UI. There is no public API for an ordinary app to accept
    /// or reject that system-owned request programmatically.
    /// </summary>
    public Task<bool> AcceptInvitationAsync(string token)
    {
        ReportError("Android gère cette demande dans sa boîte de dialogue système ; l'application ne peut pas l'accepter à sa place.");
        return Task.FromResult(false);
    }

    public Task RejectInvitationAsync(string token)
    {
        // The request belongs to Android's system UI, not to an app-owned token.
        return Task.CompletedTask;
    }

    public bool DisconnectToPeer(ref WifiDirectPeer? peer, CancellationToken token)
    {
        if (token.IsCancellationRequested || peer is null)
            return false;

        var disconnected = peer;
        peer = null;
        lock (_sync)
        {
            _connectedPeer = null;
            _connectCompletion?.TrySetCanceled();
        }
        _manager.RemoveGroup(_channel, new ActionListener(
            () => SetStatus(WifiDirectStatus.Disconnected),
            reason => ReportError($"La déconnexion Wi-Fi Direct a échoué : {reason}.")));
        ConnectionClosed?.Invoke(this, new WifiDirectPeerEventArgs(disconnected));
        return true;
    }

    public void Stop()
    {
        if (_disposed) return;
        _manager.StopPeerDiscovery(_channel, new ActionListener(() => { }, _ => { }));
        _manager.RemoveGroup(_channel, new ActionListener(() => { }, _ => { }));
        SetWatcherStatus(WifiDirectWatcherStatus.Closed);
        SetPublisherStatus(WifiDirectPublicherStatus.Closed);
        SetStatus(WifiDirectStatus.Closed);
        lock (_sync)
        {
            _connectCompletion?.TrySetCanceled();
            _connectCompletion = null;
            _connectedPeer = null;
        }
    }

    public void OpenWifiSettings()
    {
        ThrowIfDisposed();
        var action = Build.VERSION.SdkInt >= BuildVersionCodes.Q
            ? Android.Provider.Settings.Panel.ActionInternetConnectivity
            : Android.Provider.Settings.ActionWifiSettings;
        _context.StartActivity(new Intent(action).AddFlags(ActivityFlags.NewTask));
    }

    private static async Task AwaitActionAsync(Task<bool> task, CancellationToken token)
    {
        if (await task.WaitAsync(token).ConfigureAwait(false) is false)
            throw new InvalidOperationException("L'opération Wi-Fi Direct n'a pas pu démarrer.");
    }

    private void RefreshPeers()
    {
        _manager.RequestPeers(_channel, new PeerListListener(devices =>
        {
            var current = devices.DeviceList.ToDictionary(d => d.DeviceAddress, d => d);
            List<WifiDirectPeer> added;
            List<WifiDirectPeer> updated;
            List<WifiDirectPeer> removed;
            lock (_sync)
            {
                added = current.Values.Where(d => !_peers.ContainsKey(d.DeviceAddress)).Select(ToPeer).ToList();
                updated = current.Values.Where(d => _peers.ContainsKey(d.DeviceAddress)).Select(ToPeer).ToList();
                removed = _peers.Values.Where(d => !current.ContainsKey(d.DeviceAddress)).Select(ToPeer).ToList();
                _peers.Clear();
                foreach (var item in current) _peers[item.Key] = item.Value;
            }
            foreach (var peer in added) PeerDiscovered?.Invoke(this, new(peer));
            foreach (var peer in updated) PeerUpdated?.Invoke(this, new(peer));
            foreach (var peer in removed) PeerRemoved?.Invoke(this, new(peer));
        }));
    }

    private void RefreshConnection()
    {
        _manager.RequestConnectionInfo(_channel, new ConnectionInfoListener(info =>
        {
            if (!info.GroupFormed)
            {
                if (_status == WifiDirectStatus.Connected)
                {
                    WifiDirectPeer? closed;
                    lock (_sync) { closed = _connectedPeer; _connectedPeer = null; }
                    SetStatus(WifiDirectStatus.Disconnected);
                    if (closed is not null) ConnectionClosed?.Invoke(this, new(closed));
                }
                return;
            }
            _ = CompleteConnectionAsync(info);
        }));
    }

    private async Task CompleteConnectionAsync(WifiP2pInfo info)
    {
        try
        {
            string remoteAddress;
            WifiDirectPeer peer;
            bool serverMode;
            if (!info.IsGroupOwner)
            {
                remoteAddress = info.GroupOwnerAddress?.HostAddress ?? string.Empty;
                lock (_sync)
                {
                    peer = _connectingPeer ?? new WifiDirectPeer(remoteAddress, "Propriétaire du groupe");
                    serverMode = _connectingPeer is not null;
                }
                if (!IPAddress.TryParse(remoteAddress, out _))
                    throw new InvalidOperationException("L'adresse du propriétaire du groupe Wi-Fi Direct est invalide.");
            }
            else
            {
                WifiP2pDevice? remoteDevice = null;
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (remoteDevice is null && DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var group = await RequestGroupAsync().ConfigureAwait(false);
                        remoteDevice = group?.ClientList?.FirstOrDefault();
                    }
                    catch (TimeoutException) { }
                    if (remoteDevice is null)
                        await Task.Delay(400).ConfigureAwait(false);
                }

                if (remoteDevice is null)
                    throw new InvalidOperationException("Le groupe Wi-Fi Direct est formé, mais le client distant n'est pas encore visible.");

                // WifiP2pInfo intentionally does not expose the DHCP address of
                // a client. The Android TCP coordinator obtains it from the
                // remote endpoint of the accepted Windows TCP socket.
                remoteAddress = string.Empty;
                lock (_sync)
                {
                    peer = _connectingPeer ?? ToPeer(remoteDevice);
                    serverMode = _connectingPeer is not null;
                }
            }

            lock (_sync)
            {
                if (_status == WifiDirectStatus.Connected)
                    return;
                _connectedPeer = peer;
            }
            SetStatus(WifiDirectStatus.Connected);
            var args = new WifiDirectConnectionEventArgs(serverMode, remoteAddress, peer.Id);
            ConnectionEstablished?.Invoke(this, args);
            lock (_sync) _connectCompletion?.TrySetResult(args);
        }
        catch (Exception ex)
        {
            SetStatus(WifiDirectStatus.Aborded);
            ReportError(ex.Message, ex);
            lock (_sync) _connectCompletion?.TrySetException(ex);
        }
    }

    private Task<WifiP2pGroup?> RequestGroupAsync()
    {
        var completion = new TaskCompletionSource<WifiP2pGroup?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.RequestGroupInfo(_channel, new GroupInfoListener(group => completion.TrySetResult(group)));
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private void OnBroadcast(Intent? intent)
    {
        if (intent?.Action == WifiP2pManager.WifiP2pPeersChangedAction)
            RefreshPeers();
        else if (intent?.Action == WifiP2pManager.WifiP2pConnectionChangedAction)
            RefreshConnection();
        else if (intent?.Action == WifiP2pManager.WifiP2pStateChangedAction)
        {
            bool enabled = intent.GetIntExtra(WifiP2pManager.ExtraWifiState, -1) == (int)WifiP2pState.Enabled;
            if (!enabled)
            {
                SetStatus(WifiDirectStatus.Aborded);
                ReportError("Le Wi-Fi a été désactivé.");
                lock (_sync) _connectCompletion?.TrySetException(new InvalidOperationException("Wi-Fi désactivé."));
            }
            else if (_status == WifiDirectStatus.Aborded)
            {
                SetStatus(WifiDirectStatus.Disconnected);
            }
        }
        else if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu &&
                 intent?.Action == WifiP2pManager.ActionWifiP2pRequestResponseChanged)
        {
            bool accepted = intent.GetBooleanExtra(WifiP2pManager.ExtraRequestResponse, false);
            if (!accepted)
            {
                lock (_sync) _connectCompletion?.TrySetException(new InvalidOperationException("La demande Wi-Fi Direct a été refusée."));
                SetStatus(WifiDirectStatus.Disconnected);
            }
        }
    }

    private static WifiDirectPeer ToPeer(WifiP2pDevice device) =>
        new(device.DeviceAddress, string.IsNullOrWhiteSpace(device.DeviceName) ? "Appareil sans nom" : device.DeviceName);

    private void SetStatus(WifiDirectStatus value)
    {
        if (_status == value) return;
        _status = value;
        StatusChanged?.Invoke(this, new(value));
    }

    private void SetPublisherStatus(WifiDirectPublicherStatus value)
    {
        if (_publisherStatus == value) return;
        _publisherStatus = value;
        PublicherStatusChanged?.Invoke(this, new(value));
    }

    private void SetWatcherStatus(WifiDirectWatcherStatus value)
    {
        if (_watcherStatus == value) return;
        _watcherStatus = value;
        WatcherStatusChanged?.Invoke(this, new(value));
    }

    private void ReportError(string message, Exception? exception = null) =>
        Error?.Invoke(this, new(exception, message));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        if (_receiverRegistered)
        {
            _context.UnregisterReceiver(_receiver);
            _receiverRegistered = false;
        }
        lock (_sync) _peers.Clear();
        _connectionGate.Dispose();
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

    private sealed class GroupInfoListener(Action<WifiP2pGroup?> callback)
        : Java.Lang.Object, WifiP2pManager.IGroupInfoListener
    {
        public void OnGroupInfoAvailable(WifiP2pGroup? group) => callback(group);
    }

    private sealed class P2pReceiver(AndroidWifiDirectService owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) => owner.OnBroadcast(intent);
    }
}
