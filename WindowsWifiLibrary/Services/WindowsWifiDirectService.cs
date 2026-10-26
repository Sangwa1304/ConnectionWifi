using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using WindowsWifiLibrary.Interfaces;
using WindowsWifiLibrary.Models;

namespace WindowsWifiLibrary.Services 
{
    public sealed class WindowsWifiDirectService : IWifiDirectService 
    {
         
        private WiFiDirectAdvertisementPublisher? _publisher; 
        private WiFiDirectConnectionListener? _listener;
        private DeviceWatcher? _deviceWatcher;
        private readonly List<WiFiDirectDevice> _connectedDevices = new(); 
        private readonly ConcurrentDictionary<string, WiFiDirectConnectionRequest>  _pendingInvitations = new();
        private WifiDirectPublicherStatus _PublisherStatus = WifiDirectPublicherStatus.Closed;
        private WifiDirectWatcherStatus _WatcherStatus = WifiDirectWatcherStatus.Closed;


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
            _=InitializeAsync(default);
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
            try 
            {
                await Task.Delay(300);
                token.ThrowIfCancellationRequested();
                var deviceSelector = WiFiDirectDevice.GetDeviceSelector(); 
                var devices = await DeviceInformation.FindAllAsync(deviceSelector);

                CurrentStatus = devices != null ? WifiDirectStatus.Disconnected : WifiDirectStatus.NoArdwareSupported;
                return devices != null;
                
            } 
            catch (Exception ex) 
            {
                CurrentStatus = WifiDirectStatus.NoArdwareSupported;
                ReportError($"Erreur d'initialisation : {ex.Message}",ex);
                return false; 
            } 
        }
 
        public async Task StartPublishingAsync(CancellationToken token) 
        { 
             StopPublisher(); 
 
            _publisher = new WiFiDirectAdvertisementPublisher(); 
            _publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal; 
            _publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true; 
 
            _listener = new WiFiDirectConnectionListener(); 
            _listener.ConnectionRequested += OnConnectionRequested;
            _publisher.Start();
            PublisherStatus = WifiDirectPublicherStatus.Started;
        } 
 
        public async Task StartSubscribingAsync(CancellationToken token) 
        {
            StopWatcher();
            string deviceSelector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
            _deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);
            _deviceWatcher.EnumerationCompleted += _deviceWatcher_EnumerationCompleted;
            _deviceWatcher.Added += OnDeviceAdded;
            _deviceWatcher.Updated += OnDeviceUpdated; 
            _deviceWatcher.Removed += OnDeviceRemoved;
            _deviceWatcher.Start();
            WatcherStatus= WifiDirectWatcherStatus.Started;
        }

        private void _deviceWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            Debug.Write("Finiched");
        }

        public async Task StartPublisherSubscribingAsync(CancellationToken token) 
        {
            if (token.IsCancellationRequested) return;
            await StartPublishingAsync(token); 
            await StartSubscribingAsync(token);
            if (token.IsCancellationRequested) Stop();
            CurrentStatus = WifiDirectStatus.Disconnected;
        }

        public bool DisconnectToPeer(ref WifiDirectPeer? peer,CancellationToken token)
        {
            foreach (var device in _connectedDevices.ToList())
            {
                if (token.IsCancellationRequested)
                {
                     
                    return false; 
                }
                if (device.DeviceId == peer?.Id)
                {
                    device.Dispose();
                    peer = null;
                    return true;
                }
            }
            peer = null;
            return false;

        }
        public bool DisconnectToPeer(WifiDirectPeer peer, CancellationToken token)
        {
            foreach (var device in _connectedDevices.ToList())
            {
                if (token.IsCancellationRequested)
                {
                    return false;
                }
                if (device.DeviceId == peer?.Id)
                {
                    device.Dispose();
                    return true;
                }
            }
            return false;

        }
        public async Task<bool> ConnectToPeerAsync(WifiDirectPeer peer,CancellationToken token) 
        {
            try
            { 
                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(peer.Id); 
                if (wfdDevice != null) 
                { 
                    HandleSuccessfulConnection(wfdDevice,true);
                    return true;
                } 
                else 
                { 
                    ReportError($"Impossible de se connecter au pair {peer.Name}");
                    return false;
                } 
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
            string token = Guid.NewGuid().ToString(); 
 
            _pendingInvitations.TryAdd(token, request); 
 
            InvitationReceived?.Invoke(this, new 
            (
                token, 
                request 
            )); 
        } 
 
        public async Task<bool> AcceptInvitationAsync(string token) 
        { 
            if (_pendingInvitations.TryRemove(token, out var request)) 
            { 
                try 
                { 
                    WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id); 
                    if (wfdDevice != null) 
                    { 
                        HandleSuccessfulConnection(wfdDevice);
                        return true;
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
        private void HandleSuccessfulConnection(WiFiDirectDevice wfdDevice,bool ModeServerOrClient = false) 
        { 
            _connectedDevices.Add(wfdDevice);
            wfdDevice.ConnectionStatusChanged += OnDeviceConnectionStatusChanged; 
 
            var endpointPairs = wfdDevice.GetConnectionEndpointPairs(); 
            if (endpointPairs.Count > 0) 
            {
                string localIp = endpointPairs[0].LocalHostName?.CanonicalName ?? string.Empty; 
                string remoteIp = endpointPairs[0].RemoteHostName?.CanonicalName ?? string.Empty; 
                
                CurrentStatus = WifiDirectStatus.Connected;
 
                ConnectionEstablished?.Invoke(this, new (ModeServerOrClient,remoteIp, wfdDevice.DeviceId));
                StopAllToMaintainWifiDirectConnection();
            } 
            else
            {
                DisconnectToPeer(new(wfdDevice.DeviceId,""),default);
                ReportError(message:"Connexion établie mais aucune adresse IP n'a pu être assignée.");

            } 
        } 
 
        private void OnDeviceConnectionStatusChanged(WiFiDirectDevice sender, object args) 
        { 
            if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Disconnected) 
            { 
                sender.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged; 
                _connectedDevices.Remove(sender); 
                sender.Dispose(); 
 
                ConnectionClosed?.Invoke(this,new(new(sender.DeviceId,"Appareil deconnecté"))); 
                if(_connectedDevices.Count == 0)
                {
                    CurrentStatus = WifiDirectStatus.Disconnected; 
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
            StopPublisher(); 
            StopWatcher(); 
             
            foreach (var device in _connectedDevices.ToList()) 
            {
                device.Dispose();
            }
            _connectedDevices.Clear(); 
            _pendingInvitations.Clear(); 
 
            CurrentStatus = WifiDirectStatus.Closed; 
        } 
 
        private void StopPublisher() 
        { 
            if (_publisher != null) 
            { 
                _publisher?.Stop(); 
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
            Stop();
        }

        private void StopAllToMaintainWifiDirectConnection()
        {
            StopPublisher();
            StopWatcher();
        }

        public bool IsConnectToPeer(WifiDirectPeer target)
        {
            foreach(var peer in _connectedDevices)
            {
                if(peer.DeviceId == target.Id)
                {
                    return true;
                }
            }
            return false;
        }
    }
}