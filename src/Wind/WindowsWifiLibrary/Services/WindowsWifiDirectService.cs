using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Devices.WiFiDirect;
using Windows.Networking.Connectivity;
using WindowsWifiLibrary.Interfaces;
using WindowsWifiLibrary.Models;

namespace WindowsWifiLibrary.Services 
{
    public sealed class WindowsWifiDirectService : IWifiDirectService 
    {
        private Radio? _wifiRadio;
        public async Task<bool> WifiExists()
        {
            ThrowIfDisposed();
            Radio? radio = (await Radio.GetRadiosAsync())?.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            lock (_sync)
            {
                if (!ReferenceEquals(_wifiRadio, radio))
                {
                    if (_radioStateHandlerAttached && _wifiRadio is not null)
                    {
                        _wifiRadio.StateChanged -= WindowsWifiDirectService_StateChanged;
                    }

                    _wifiRadio = radio;
                    _radioStateHandlerAttached = false;
                }

                if (_wifiRadio is not null && !_radioStateHandlerAttached)
                {
                    _wifiRadio.StateChanged += WindowsWifiDirectService_StateChanged;
                    _radioStateHandlerAttached = true;
                }
            }

            return _wifiRadio != null;
        }

        private WiFiDirectAdvertisementPublisher? _publisher;
        private WiFiDirectConnectionListener? _listener;
        private DeviceWatcher? _deviceWatcher;
        private readonly List<WiFiDirectDevice> _connectedDevices = new(); 
        private readonly ConcurrentDictionary<string, WiFiDirectConnectionRequest>  _pendingInvitations = new();
        private WifiDirectPublicherStatus _PublisherStatus = WifiDirectPublicherStatus.Closed;
        private WifiDirectWatcherStatus _WatcherStatus = WifiDirectWatcherStatus.Closed;
        private readonly object _sync = new();
        private bool _radioStateHandlerAttached;
        private bool _disposed;


        public WifiDirectPublicherStatus PublisherStatus 
        {
            get => _PublisherStatus; 
            private set
            {
                if(_PublisherStatus != value)
                {
                    _PublisherStatus = value;
                    PublicherStatusChanged?.Invoke(this, new(_PublisherStatus));
                }
            }
        }
        public WifiDirectWatcherStatus WatcherStatus 
        { 
            get=>_WatcherStatus; 
            private set
            {
                if (_WatcherStatus != value)
                {
                    _WatcherStatus = value;
                    WatcherStatusChanged?.Invoke(this, new(_WatcherStatus));
                }
            }
        }

        private WifiDirectStatus _DirectStatus  = WifiDirectStatus.Closed;
        
        public WindowsWifiDirectService()
        {
        }
        public WifiDirectStatus CurrentStatus
        {
            get => _DirectStatus;

            private set
            {
                if(_DirectStatus != value)
                {
                    _DirectStatus = value;
                    StatusChanged?.Invoke(this, new(_DirectStatus));
                }
                
            }
        }

        public event EventHandler<WifiDirectPublicherStatusEventArgs>? PublicherStatusChanged;
        public event EventHandler<WifiDirectWatcherStatusEventArgs>? WatcherStatusChanged;
        public event EventHandler<WifiDirectStatusEventArgs>? StatusChanged; 
        public event EventHandler<WifiDirectPeerEventArgs>? PeerDiscovered; 
        public event EventHandler<WifiDirectPeerEventArgs>? PeerUpdated; 
        public event EventHandler<WifiDirectPeerEventArgs>? PeerRemoved; 
        public event EventHandler<WifiDirectInvitationEventArgs>? InvitationReceived; 
        public event EventHandler<WifiDirectConnectionEventArgs>? ConnectionEstablished;
        public event EventHandler<WifiDirectPeerEventArgs>? ConnectionClosed; 
        public event EventHandler<WifiDirectErrorEventArgs>? Error; 
 
        public async Task<bool> InitializeAsync(CancellationToken token) 
        { 
            ThrowIfDisposed();
            try 
            {
                if(await WifiExists())
                {
                    if(_wifiRadio?.State != RadioState.On)
                    {
                        throw new Exception("le wifi est Desactivé");
                    }
                }
                else
                {
                    throw new Exception("le wifi est indisponible");
                }
                await Task.Delay(300, token);
                token.ThrowIfCancellationRequested();
                var deviceSelector = WiFiDirectDevice.GetDeviceSelector(); 
                var devices = await DeviceInformation.FindAllAsync(deviceSelector);
                token.ThrowIfCancellationRequested();

                CurrentStatus = devices is not null ? WifiDirectStatus.Disconnected : WifiDirectStatus.NoArdwareSupported;
                return devices is not null;
                
            } 
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) 
            {
                CurrentStatus = WifiDirectStatus.NoArdwareSupported;
                ReportError($"Erreur d'initialisation : {ex.Message}",ex);
                return false;
            } 
        }
 
        public Task StartPublishingAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            try
            {
                lock (_sync)
                {
                    StopPublisher();
                    WiFiDirectAdvertisementPublisher publisher = new();
                    WiFiDirectConnectionListener listener = new();
                    listener.ConnectionRequested += OnConnectionRequested;
                    publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
                    publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
                    publisher.Start();
                    _publisher = publisher;
                    _listener = listener;
                    PublisherStatus = WifiDirectPublicherStatus.Started;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                PublisherStatus = WifiDirectPublicherStatus.Closed;
                ReportError("Erreur de démarrage de la publication Wi-Fi Direct.", ex);
            }

            return Task.CompletedTask;
        }

        private void WindowsWifiDirectService_StateChanged(Radio sender, object args)
        {
            if (_disposed)
            {
                return;
            }

            if(_wifiRadio?.State == RadioState.On)
            {
                _ = StartPublisherSubscribingAsync(default);
            }
            else if (_wifiRadio?.State == RadioState.Off)
            {
                StopButWifiOff();
                StopPublisher();
                StopWatcher();
            }
        }

        public Task StartSubscribingAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            token.ThrowIfCancellationRequested();
            
            try
            {
                lock (_sync)
                {
                    StopWatcher();
                    string deviceSelector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
                    DeviceWatcher watcher = DeviceInformation.CreateWatcher(deviceSelector);
                    watcher.EnumerationCompleted += _deviceWatcher_EnumerationCompleted;
                    watcher.Added += OnDeviceAdded;
                    watcher.Updated += OnDeviceUpdated;
                    watcher.Removed += OnDeviceRemoved;
                    watcher.Start();
                    _deviceWatcher = watcher;
                    WatcherStatus = WifiDirectWatcherStatus.Started;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                WatcherStatus = WifiDirectWatcherStatus.Closed;
                ReportError("Erreur de démarrage de la découverte Wi-Fi Direct.", ex);
            }

            return Task.CompletedTask;
        }

        private void _deviceWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            Debug.Write("Finiched");
        }

        public async Task StartPublisherSubscribingAsync(CancellationToken token) 
        {
            if (_disposed || token.IsCancellationRequested) return;
            try
            {
                await StartPublishingAsync(token);
                await StartSubscribingAsync(token);
                if (!token.IsCancellationRequested && !_disposed)
                {
                    CurrentStatus = WifiDirectStatus.Disconnected;
                }

                if (token.IsCancellationRequested && !_disposed)
                {
                    Stop();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_disposed)
            {
            }
            catch (Exception ex)
            {
                ReportError("Erreur de restauration du service Wi‑Fi Direct.", ex);
            }
        }

        public bool DisconnectToPeer(ref WifiDirectPeer? peer,CancellationToken token)
        {
            if (token.IsCancellationRequested || peer is null)
            {
                return false;
            }

            bool disconnected = DisconnectToPeer(peer, token);
            peer = null;
            return disconnected;
        }
        public bool DisconnectToPeer(WifiDirectPeer peer, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            bool noConnectedDevices;
            lock (_sync)
            {
                WiFiDirectDevice? device = _connectedDevices.FirstOrDefault(item => item.DeviceId == peer.Id);
                if (device is null)
                {
                    return false;
                }

                _connectedDevices.Remove(device);
                device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                device.Dispose();
                noConnectedDevices = _connectedDevices.Count == 0;
            }

            ConnectionClosed?.Invoke(this, new(new(peer.Id, peer.Name)));

            if (noConnectedDevices)
            {
                CurrentStatus = WifiDirectStatus.Disconnected;
                _ = StartPublishingAsync(CancellationToken.None);
            }

            return true;
        }
        public async Task<bool> ConnectToPeerAsync(WifiDirectPeer peer,CancellationToken token) 
        {
            ThrowIfDisposed();
            try
            { 
                token.ThrowIfCancellationRequested();
                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(peer.Id); 
                token.ThrowIfCancellationRequested();
                if (wfdDevice != null) 
                { 
                    return HandleSuccessfulConnection(wfdDevice, true);
                } 
                else 
                { 
                    ReportError($"Impossible de se connecter au pair {peer.Name}");
                    return false;
                } 
            } 
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) 
            { 
                ReportError($"Erreur de connexion : {ex.Message}");
                return false;
            } 
        } 
 
        private void OnConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args) 
        { 
            var request = args.GetConnectionRequest(); 
            string token = Guid.NewGuid().ToString("N");

            lock (_sync)
            {
                if (_disposed || _wifiRadio?.State != RadioState.On ||
                    !_pendingInvitations.TryAdd(token, request))
                {
                    request.Dispose();
                    return;
                }
            }
 
            InvitationReceived?.Invoke(this, new 
            (
                token,
                new WifiDirectPeer(request.DeviceInformation.Id, request.DeviceInformation.Name)
            )); 
        } 
 
        public async Task<bool> AcceptInvitationAsync(string token) 
        {
            ThrowIfDisposed();
            if (_pendingInvitations.TryRemove(token, out var request)) 
            { 
                try 
                { 
                    WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id); 
                    if (wfdDevice != null) 
                    { 
                        return HandleSuccessfulConnection(wfdDevice);
                    } 
                    else 
                    { 
                        ReportError(message:"L'appareil distant s'est déconnecté."); 
                    } 
                } 
                catch (Exception ex) 
                { 
                    ReportError($"Erreur d'acceptation : {ex.Message}",ex); 
                } 
                finally
                {
                    request.Dispose();
                }
            } 
            else 
            { 
                ReportError(message:"Token d'invitation invalide."); 
            }
            return false;
        } 
 
        public Task RejectInvitationAsync(string token) 
        { 
            if (_pendingInvitations.TryRemove(token, out var request)) 
            { 
                request.Dispose(); 
            }
            return Task.CompletedTask; 
        }
        private bool HandleSuccessfulConnection(WiFiDirectDevice wfdDevice,bool ModeServerOrClient = false)
        { 
            lock (_sync)
            {
                if (_connectedDevices.Any(device => device.DeviceId == wfdDevice.DeviceId))
                {
                    wfdDevice.Dispose();
                    return true;
                }

                _connectedDevices.Add(wfdDevice);
            }
            wfdDevice.ConnectionStatusChanged += OnDeviceConnectionStatusChanged; 
 
            var endpointPairs = wfdDevice.GetConnectionEndpointPairs(); 
            if (endpointPairs.Count > 0) 
            {
                string remoteIp = endpointPairs[0].RemoteHostName?.CanonicalName ?? string.Empty; 

                if (!IPAddress.TryParse(remoteIp, out _))
                {
                    DisconnectToPeer(new(wfdDevice.DeviceId, ""), default);
                    ReportError(message: "La connexion Wi‑Fi Direct ne fournit pas d’adresse IP distante valide.");
                    return false;
                }
                
                CurrentStatus = WifiDirectStatus.Connected;
 
                ConnectionEstablished?.Invoke(this, new (ModeServerOrClient,remoteIp, wfdDevice.DeviceId));
                StopAllToMaintainWifiDirectConnection();
                return true;
            } 
            else
            {
                DisconnectToPeer(new(wfdDevice.DeviceId,""),default);
                ReportError(message:"Connexion établie mais aucune adresse IP n'a pu être assignée.");
                return false;
            } 
        } 
 
        private void OnDeviceConnectionStatusChanged(WiFiDirectDevice sender, object args) 
        { 
            if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Disconnected) 
            { 
                sender.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged; 
                bool noConnectedDevices;
                lock (_sync)
                {
                    _connectedDevices.Remove(sender);
                }
                sender.Dispose(); 
 
                ConnectionClosed?.Invoke(this,new(new(sender.DeviceId,"Appareil deconnecté"))); 
                lock (_sync)
                {
                    noConnectedDevices = _connectedDevices.Count == 0;
                }

                if(noConnectedDevices)
                {
                    CurrentStatus = WifiDirectStatus.Disconnected;
                    _ = StartPublishingAsync(CancellationToken.None);
                }
            }
        }
 
        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args) 
        { 
            PeerDiscovered?.Invoke(this, new WifiDirectPeerEventArgs 
             (
                new (args.Id,string.IsNullOrEmpty(args.Name) ?"Appareil Inconnu" : args.Name ) 
            )); 
        } 
 
        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate args) 
        { 
            PeerUpdated?.Invoke(this, new 
            (
                new ( args.Id,"Appareil mis à jour" ) 
            )); 
        } 
 
        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args) 
        { 
            PeerRemoved?.Invoke(this, new 
            ( 
                new (args.Id,"appareil disparue" )
            )); 
        } 
 
        public void Stop() 
        { 
            lock (_sync)
            {
                StopPublisher();
                StopWatcher();

                foreach (var device in _connectedDevices)
                {
                    device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                    device.Dispose();
                }
                _connectedDevices.Clear();

                foreach (KeyValuePair<string, WiFiDirectConnectionRequest> invitation in _pendingInvitations)
                {
                    if (_pendingInvitations.TryRemove(invitation.Key, out WiFiDirectConnectionRequest? request))
                    {
                        request.Dispose();
                    }
                }
            }

            CurrentStatus = WifiDirectStatus.Closed; 
        } 
        private void StopButWifiOff()
        {
            List<WifiDirectPeer> disconnectedPeers = [];
            lock (_sync)
            {
                StopPublisher();
                StopWatcher();

                foreach (WiFiDirectDevice device in _connectedDevices)
                {
                    device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                    disconnectedPeers.Add(new(device.DeviceId, "Wi‑Fi désactivé"));
                    device.Dispose();
                }
                _connectedDevices.Clear();

                foreach (KeyValuePair<string, WiFiDirectConnectionRequest> invitation in _pendingInvitations)
                {
                    if (_pendingInvitations.TryRemove(invitation.Key, out WiFiDirectConnectionRequest? request))
                    {
                        request.Dispose();
                    }
                }
            }

            CurrentStatus = WifiDirectStatus.NoArdwareSupported;
            foreach (WifiDirectPeer peer in disconnectedPeers)
            {
                ConnectionClosed?.Invoke(this, new(peer));
            }
        }
        private void StopPublisher() 
        {
            if (_publisher != null) 
            {
                try
                {
                    _publisher?.Stop(); 
                }
                catch(Exception)
                {
                }
                _publisher = null; 
            } 
            if (_listener != null) 
            { 
                _listener.ConnectionRequested -= OnConnectionRequested;
                _listener = null; 
            }
            PublisherStatus = WifiDirectPublicherStatus.Closed;
        } 
 
        private void StopWatcher()
        {
            if (_deviceWatcher != null) 
            {
                _deviceWatcher.EnumerationCompleted -= _deviceWatcher_EnumerationCompleted;
                _deviceWatcher.Added -= OnDeviceAdded;
                _deviceWatcher.Updated -= OnDeviceUpdated;
                _deviceWatcher.Removed -= OnDeviceRemoved;
                _deviceWatcher.Stop();
                _deviceWatcher = null;
            }
            WatcherStatus = WifiDirectWatcherStatus.Closed;
        } 
 
        private void ReportError(string message, Exception? error = null) 
        { 
            Error?.Invoke(this, new (error, error?.Message?? message));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
            lock (_sync)
            {
                if (_radioStateHandlerAttached && _wifiRadio is not null)
                {
                    _wifiRadio.StateChanged -= WindowsWifiDirectService_StateChanged;
                    _radioStateHandlerAttached = false;
                }
            }
        }

        private void StopAllToMaintainWifiDirectConnection()
        {
            StopPublisher();
            StopWatcher();
        }

        public bool IsConnectToPeer(WifiDirectPeer target)
        {
            lock (_sync)
            {
                foreach(var peer in _connectedDevices)
                {
                    if(peer.DeviceId == target.Id)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
