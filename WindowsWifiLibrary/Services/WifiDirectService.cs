using System.Collections.Concurrent;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using WindowsWifiLibrary.Interfaces;
using WindowsWifiLibrary.Models;

namespace WindowsWifiLibrary.Services 
{ 
    public class WindowsWifiDirectService : IWifiDirectService 
    { 
        private WiFiDirectAdvertisementPublisher? _publisher; 
        private WiFiDirectConnectionListener? _listener; 
        private DeviceWatcher? _deviceWatcher; 
         
        private readonly List<WiFiDirectDevice> _connectedDevices = new(); 
        private readonly ConcurrentDictionary<string, WiFiDirectConnectionRequest> 
_pendingInvitations = new(); 
 
        public WifiDirectStatus CurrentStatus { get; private set; } = WifiDirectStatus.Idle; 
        public bool IsHardwareSupported { get; private set; } 
 
        public event EventHandler<WifiDirectStatusEventArgs>? StatusChanged; 
        public event EventHandler<WifiDirectPeerEventArgs>? PeerDiscovered; 
        public event EventHandler<WifiDirectPeerEventArgs>? PeerUpdated; 
        public event EventHandler<WifiDirectPeerEventArgs>? PeerRemoved; 
        public event EventHandler<WifiDirectInvitationEventArgs>? InvitationReceived; 
        public event EventHandler<WifiDirectConnectionEventArgs>? ConnectionEstablished; 
        public event EventHandler<WifiDirectConnectionEventArgs>? ConnectionClosed; 
        public event EventHandler<WifiDirectErrorEventArgs>? Error; 
 
        public async Task<bool> InitializeAsync() 
        { 
            try 
            { 
                var deviceSelector = WiFiDirectDevice.GetDeviceSelector(); 
                var devices = await DeviceInformation.FindAllAsync(deviceSelector); 
                IsHardwareSupported = devices != null; 
                UpdateStatus(WifiDirectStatus.Idle); 
                return IsHardwareSupported; 
            } 
            catch (Exception ex) 
            { 
                ReportError($"Erreur d'initialisation : {ex.Message}"); 
                IsHardwareSupported = false; 
                return false; 
            } 
        } 
 
        public Task StartPublishingAsync(string serviceName, string deviceName) 
        { 
            return Task.Run(() => 
            { 
                StopPublisher(); 
 
                _publisher = new WiFiDirectAdvertisementPublisher(); 
                _publisher.Advertisement.ListenStateDiscoverability = 
WiFiDirectAdvertisementListenStateDiscoverability.Normal; 
                _publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true; 
 
                _listener = new WiFiDirectConnectionListener(); 
                _listener.ConnectionRequested += OnConnectionRequested; 
 
                _publisher.Start(); 
                UpdateStatus(WifiDirectStatus.Publishing); 
            }); 
        } 
 
        public Task StartSubscribingAsync(string serviceName) 
        { 
            return Task.Run(() => 
            { 
                StopWatcher(); 
 
                string deviceSelector = WiFiDirectDevice.GetDeviceSelector(); 
                _deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector); 
 
                _deviceWatcher.Added += OnDeviceAdded; 
                _deviceWatcher.Updated += OnDeviceUpdated; 
                _deviceWatcher.Removed += OnDeviceRemoved; 
 
                _deviceWatcher.Start(); 
                UpdateStatus(WifiDirectStatus.Subscribing); 
            }); 
        } 
 
        public async Task StartPublisherSubscribingAsync(string serviceName, string 
deviceName) 
        { 
            await StartPublishingAsync(serviceName, deviceName); 
            await StartSubscribingAsync(serviceName); 
        } 
 
        public async Task ConnectToPeerAsync(WifiDirectPeer peer) 
        { 
            try 
            { 
                WiFiDirectDevice wfdDevice = await WiFiDirectDevice.FromIdAsync(peer.Id); 
                if (wfdDevice != null) 
                { 
                    HandleSuccessfulConnection(wfdDevice); 
                } 
                else 
                { 
                    ReportError($"Impossible de se connecter au pair {peer.Name}"); 
                } 
            } 
            catch (Exception ex) 
            { 
                ReportError($"Erreur de connexion : {ex.Message}"); 
            } 
        } 
 
        private void OnConnectionRequested(WiFiDirectConnectionListener sender, 
WiFiDirectConnectionRequestedEventArgs args) 
        { 
            var request = args.GetConnectionRequest(); 
            string token = Guid.NewGuid().ToString(); 
 
            _pendingInvitations.TryAdd(token, request); 
 
            InvitationReceived?.Invoke(this, new WifiDirectInvitationEventArgs 
            { 
                Token = token, 
                Request = request 
            }); 
        } 
 
        public async Task AcceptInvitationAsync(string token) 
        { 
            if (_pendingInvitations.TryRemove(token, out var request)) 
            { 
                try 
                { 
                    WiFiDirectDevice wfdDevice = await 
WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id); 
                    if (wfdDevice != null) 
                    { 
                        HandleSuccessfulConnection(wfdDevice); 
                    } 
                    else 
                    { 
                        ReportError("L'appareil distant s'est déconnecté."); 
                    } 
                } 
                catch (Exception ex) 
                { 
                    ReportError($"Erreur d'acceptation : {ex.Message}"); 
                } 
            } 
            else 
            { 
                ReportError("Token d'invitation invalide."); 
            } 
        } 
 
        public Task RejectInvitationAsync(string token) 
        { 
            if (_pendingInvitations.TryRemove(token, out var request)) 
            { 
                request.Dispose(); 
            } 
            return Task.CompletedTask; 
        } 
 
        private void HandleSuccessfulConnection(WiFiDirectDevice wfdDevice) 
        { 
            _connectedDevices.Add(wfdDevice); 
            wfdDevice.ConnectionStatusChanged += OnDeviceConnectionStatusChanged; 
 
            var endpointPairs = wfdDevice.GetConnectionEndpointPairs(); 
            if (endpointPairs.Count > 0) 
            { 
                string localIp = endpointPairs[0].LocalHostName?.CanonicalName ?? string.Empty; 
                string remoteIp = endpointPairs[0].RemoteHostName?.CanonicalName ?? 
string.Empty; 
 
                UpdateStatus(WifiDirectStatus.Connected); 
 
                ConnectionEstablished?.Invoke(this, new WifiDirectConnectionEventArgs 
                { 
                    LocalIpAddress = localIp, 
                    RemoteIpAddress = remoteIp 
                }); 
            } 
            else 
            { 
                ReportError("Connexion établie mais aucune adresse IP n'a pu être assignée."); 
            } 
        } 
 
        private void OnDeviceConnectionStatusChanged(WiFiDirectDevice sender, object args) 
        { 
            if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Disconnected) 
            { 
                sender.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged; 
                _connectedDevices.Remove(sender); 
                sender.Dispose(); 
 
                ConnectionClosed?.Invoke(this, new WifiDirectConnectionEventArgs()); 
                UpdateStatus(WifiDirectStatus.Idle); 
            } 
        } 
 
        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args) 
        { 
            PeerDiscovered?.Invoke(this, new WifiDirectPeerEventArgs 
            { 
                Peer = new WifiDirectPeer { Id = args.Id, Name = string.IsNullOrEmpty(args.Name) ? 
"Appareil Inconnu" : args.Name } 
            }); 
        } 
 
        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate args) 
        { 
            PeerUpdated?.Invoke(this, new WifiDirectPeerEventArgs 
            { 
                Peer = new WifiDirectPeer { Id = args.Id, Name = "Appareil mis à jour" } 
            }); 
        } 
 
        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate args) 
        { 
            PeerRemoved?.Invoke(this, new WifiDirectPeerEventArgs 
            { 
                Peer = new WifiDirectPeer { Id = args.Id } 
            }); 
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
 
            UpdateStatus(WifiDirectStatus.Idle); 
        } 
 
        private void StopPublisher() 
        { 
            if (_publisher != null) 
            { 
                _publisher.Stop(); 
                _publisher = null; 
            } 
            if (_listener != null) 
            { 
                _listener.ConnectionRequested -= OnConnectionRequested; 
                _listener = null; 
            } 
        } 
 
        private void StopWatcher() 
        { 
            if (_deviceWatcher != null) 
            { 
                _deviceWatcher.Added -= OnDeviceAdded; 
                _deviceWatcher.Updated -= OnDeviceUpdated; 
                _deviceWatcher.Removed -= OnDeviceRemoved; 
                _deviceWatcher.Stop(); 
                _deviceWatcher = null; 
            } 
        } 
 
        private void UpdateStatus(WifiDirectStatus newStatus) 
        { 
            CurrentStatus = newStatus; 
            StatusChanged?.Invoke(this, new WifiDirectStatusEventArgs { Status = newStatus }); 
        } 
 
        private void ReportError(string message) 
        { 
            UpdateStatus(WifiDirectStatus.Error); 
            Error?.Invoke(this, new WifiDirectErrorEventArgs { Message = message }); 
        } 
 
        public void Dispose() => Stop(); 
    }
}