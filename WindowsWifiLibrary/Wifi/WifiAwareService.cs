using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect.Services;
using Windows.Security.Cryptography;

namespace WindowsWifiLibrary.Wifi;

public class WifiAwareService : IWifiAwareService
{
    private readonly object _lock = new object();
    private bool _isDisposed;

    // API Windows Wi-Fi Direct
    private WiFiDirectServiceAdvertiser _advertiser;
    private WiFiDirectServiceSubscriber _subscriber;

    // Stockage et gestion des sessions actives et requêtes d'invitation
    private readonly List<WiFiDirectServiceSession> _activeSessions = new List<WiFiDirectServiceSession>();
    private readonly ConcurrentDictionary<string, WiFiDirectServiceSessionRequest> _pendingInvitations = new ConcurrentDictionary<string, WiFiDirectServiceSessionRequest>();
    private readonly ConcurrentDictionary<string, WifiAwarePeer> _discoveredPeers = new ConcurrentDictionary<string, WifiAwarePeer>();

    // Propriétés de l'interface
    private WifiAwareStatus _currentStatus = WifiAwareStatus.None;
    public WifiAwareStatus CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (_currentStatus != value)
            {
                _currentStatus = value;
                StatusChanged?.Invoke(this, _currentStatus);
            }
        }
    }

    public bool IsHardwareSupported { get; private set; }

    // Événements de l'interface
    public event EventHandler<WifiAwareStatus> StatusChanged;
    public event EventHandler<WifiAwarePeerEventArgs> PeerDiscovered;
    public event EventHandler<WifiAwarePeerEventArgs> PeerUpdated; // Wi-Fi Direct gère l'ajout/perte, l'update est simulé
    public event EventHandler<WifiAwarePeerEventArgs> PeerRemoved;
    public event EventHandler<WifiAwareInvitationEventArgs> InvitationReceived;
    public event EventHandler<WifiAwareConnectionEventArgs> ConnectionEstablished;

    public WifiAwareService()
    {
        CurrentStatus = WifiAwareStatus.None;
    }

    /// <summary>
    /// Initialise le service et vérifie si le matériel supporte le Wi-Fi Direct / NAN.
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        ThrowIfDisposed();
        CurrentStatus = WifiAwareStatus.Initializing;
        
        try
        {
            // Windows requiert de tester l'instanciation ou de requérir les capacités du sélecteur de système.
            // On tente une instanciation rapide d'un Advertiser virtuel pour valider le support matériel.
            var selector = WiFiDirectServiceAdvertiser. //GetDeviceSelector("TestService");
            var devices = await DeviceInformation.FindAllAsync(selector);
    
            // Si aucune exception n'est jetée, le protocole est supporté par le système.
            IsHardwareSupported = true;
            CurrentStatus = WifiAwareStatus.Ready;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is COMException)
        {
            IsHardwareSupported = false;
            CurrentStatus = WifiAwareStatus.HardwareNotSupported;
            return false;
        }
        catch (Exception ex)
        {
            HandleException(ex);
            return false;
        }
    }

    /// <summary>
    /// Démarre la publication (Advertising) du service.
    /// </summary>
    public async Task StartPublishingAsync(string sName, string dName)
    {
        ThrowIfDisposed();
        if (!IsHardwareSupported) throw new InvalidOperationException("Le matériel ne supporte pas le Wi-Fi Aware/Direct.");

        Stop(); // Nettoyage préventif

        try
        {
            lock (_lock)
            {
                _advertiser = new WiFiDirectServiceAdvertiser(sName);
        
                if (!string.IsNullOrEmpty(dName))
                {
                    // Encodage du Display Name dans les informations personnalisées du service
                    var buffer = CryptographicBuffer.ConvertStringToBinary(dName, BinaryStringEncoding.Utf8);
                    _advertiser.ServiceInfo = buffer;
                }

                _advertiser.SessionRequested += Advertiser_SessionRequested;
                _advertiser.AdvertisementStatusChanged += Advertiser_StatusChanged;
            
                _advertiser.Start();
            }

            CurrentStatus = WifiAwareStatus.Publishing;
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            HandleException(ex);
            throw;
        }
    }

    /// <summary>
    /// Démarre la recherche (Subscription) de services publiés.
    /// </summary>
    public async Task StartSubscribingAsync(string sName)
    {
        ThrowIfDisposed();
        if (!IsHardwareSupported) throw new InvalidOperationException("Le matériel ne supporte pas le Wi-Fi Aware/Direct.");

        Stop(); // Nettoyage préventif

        try
        {
            lock (_lock)
            {
                _subscriber = new WiFiDirectServiceSubscriber(sName);
                _subscriber.ServiceAdded += Subscriber_ServiceAdded;
                _subscriber.ServiceRemoved += Subscriber_ServiceRemoved;
        
                _subscriber.Start();
            }

            CurrentStatus = WifiAwareStatus.Subscribing;
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            HandleException(ex);
            throw;
        }
    }

    /// <summary>
    /// Connexion active initiée par l'abonné vers un pair découvert.
    /// </summary>
    public async Task ConnectToPeerAsync(WifiAwarePeer peer)
    {
        ThrowIfDisposed();
        if (peer == null || peer.DiscoveredInfo == null) throw new ArgumentNullException(nameof(peer));

        try
        {
            WiFiDirectServiceSession session = null;
            lock (_lock)
            {
                if (_subscriber == null) 
                    throw new InvalidOperationException("La connexion nécessite d'avoir initié une souscription active.");
            }

            // Établissement de la session Wi-Fi Direct Service
            session = await _subscriber.ConnectAsync(peer.DiscoveredInfo.ServiceDeviceInformation);
    
            if (session != null)
            {
                HandleNewSession(session);
            }
        }
        catch (Exception ex)
        {
            HandleException(ex);
            throw;
        }
    }

    /// <summary>
    /// Accepte une invitation de connexion reçue (Advertiser side).
    /// </summary>
    public async Task AcceptInvitationAsync(string token)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("Le token ne peut pas être nul ou vide.", nameof(token));

        if (_pendingInvitations.TryRemove(token, out WiFiDirectServiceSessionRequest request))
        {
            try
            {
                var session = await request.AcceptAsync();
                if (session != null)
                {
                    HandleNewSession(session);
                }
            }
            catch (Exception ex)
            {
                HandleException(ex);
                throw;
            }
        }
        else
        {
            throw new InvalidOperationException("Aucune invitation pendante correspondante à ce token.");
        }
    }

    /// <summary>
    /// Rejette l'invitation en détruisant proprement la requête.
    /// </summary>
    public async Task RejectInvitationAsync(string token)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("Le token de rejet est invalide.", nameof(token));

        if (_pendingInvitations.TryRemove(token, out var request))
        {
            try
            {
                request.Dispose(); // Disposer la requête annule la demande côté client
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                HandleException(ex);
            }
        }
    }

    /// <summary>
    /// Arrête proprement toutes les publications, écoutes et réinitialise l'état.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                if (_advertiser != null)
                {
                    _advertiser.SessionRequested -= Advertiser_SessionRequested;
                    _advertiser.AdvertiserStatusChanged -= Advertiser_StatusChanged;
                    
                    _advertiser.Stop();
                    _advertiser = null;
                }

                if (_subscriber != null)
                {
                    _subscriber.ServiceAdded -= Subscriber_ServiceAdded;
                    _subscriber.ServiceRemoved -= Subscriber_ServiceRemoved;
                    _subscriber.Stop();
                    _subscriber = null;
                }

                // Libération des requêtes pendantes non résolues
                foreach (var token in _pendingInvitations.Keys)
                {
                    if (_pendingInvitations.TryRemove(token, out var request))
                    {
                        request.Dispose();
                    }
                }

                // Fermeture de toutes les sockets de sessions actives
                foreach (var session in _activeSessions)
                {
                    session.SessionStatusChanged -= Session_SessionStatusChanged;
                    session.Dispose();
                }
                _activeSessions.Clear();
        
                // Vider les pairs découverts
                foreach (var peer in _discoveredPeers.Values)
                {
                    PeerRemoved?.Invoke(this, new WifiAwarePeerEventArgs(peer));
                }
                _discoveredPeers.Clear();

                if (IsHardwareSupported)
                    CurrentStatus = WifiAwareStatus.Ready;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erreur durant le Stop: {ex.Message}");
            }
        }
    }

    #region Event Handlers de l'API Windows

    private void Advertiser_StatusChanged(WiFiDirectServiceAdvertiser sender, object args)
    {
        if (sender.Status == WiFiDirectServiceAdvertiserStatus.Aborted)
        {
            CurrentStatus = WifiAwareStatus.Error;
        }
    }

    private void Subscriber_ServiceAdded(WiFiDirectServiceSubscriber sender, WiFiDirectServiceDiscoveredInformation args)
    {
        try
        {
            var peerId = args.ServiceDeviceInformation.Id;
            var peer = new WifiAwarePeer
            {
                Id = peerId,
                Name = args.ServiceDeviceInformation.Name,
                ServiceName = sender.ServiceName,
                DiscoveredInfo = args
            };

            if (_discoveredPeers.TryAdd(peerId, peer))
            {
                PeerDiscovered?.Invoke(this, new WifiAwarePeerEventArgs(peer));
            }
            else
            {
                _discoveredPeers[peerId] = peer;
                PeerUpdated?.Invoke(this, new WifiAwarePeerEventArgs(peer));
            }
        }
        catch (Exception ex)
        {
            HandleException(ex);
        }
    }

    private void Advertiser_SessionRequested(WiFiDirectServiceAdvertiser sender, WiFiDirectServiceSessionRequestedEventArgs args)
    {
        try
        {
            var request = args.GetSessionRequest();
            var token = Guid.NewGuid().ToString();

            if (_pendingInvitations.TryAdd(token, request))
            {
                InvitationReceived?.Invoke(this, new WifiAwareInvitationEventArgs
                {
                    Token = token,
                    SenderName = request.DeviceInformation?.Name ?? "Appareil Wi-Fi Direct Inconnu"
                });
            }
        }
        catch (Exception ex)
        {
            HandleException(ex);
        }
    }

    private void Subscriber_ServiceRemoved(WiFiDirectServiceSubscriber sender, WiFiDirectServiceDiscoveredEventArgs args)
    {
        try
        {
            var peerId = args.ServiceDeviceInformation.Id;
            if (_discoveredPeers.TryRemove(peerId, out var peer))
                PeerRemoved?.Invoke(this, new WifiAwarePeerEventArgs(peer));
        }
        catch (Exception ex)
        {
            HandleException(ex);
        }
    }

    private void Session_SessionStatusChanged(WiFiDirectServiceSession sender, object args)
    {
        if (sender.Status == WiFiDirectServiceSessionStatus.Closed)
        {
            lock (_lock)
            {
                _activeSessions.Remove(sender);
                sender.SessionStatusChanged -= Session_SessionStatusChanged;
                sender.Dispose();
            }
            if (_activeSessions.Count == 0 && CurrentStatus == WifiAwareStatus.Connected)
            {
                CurrentStatus = IsHardwareSupported ? WifiAwareStatus.Ready : WifiAwareStatus.None;
            }
        }
    }

    #endregion

    #region Gestion de la connexion et extraction des adresses IP

    private void HandleNewSession(WiFiDirectServiceSession session)
    {
        lock (_lock)
        {
            _activeSessions.Add(session);
            session.SessionStatusChanged += Session_SessionStatusChanged;
        }

        CurrentStatus = WifiAwareStatus.Connected;

        // Extraction des adresses IPv4 et IPv6 Link-Local
        ExtractIPAddresses(session, out var localIPv4, out var remoteIPv4, out var localIPv6, out var remoteIPv6);

        ConnectionEstablished?.Invoke(this, new WifiAwareConnectionEventArgs
        {
            SessionId = session.SessionId.ToString(),
            LocalIPv4 = localIPv4,
            RemoteIPv4 = remoteIPv4,
            LocalIPv6 = localIPv6,
            RemoteIPv6 = remoteIPv6,
            RawSession = session
        });
    }

    /// <summary>
    /// Analyse les EndpointPairs de la session Wi-Fi Direct pour mapper l'IPv4 et l'IPv6 Link-Local.
    /// Les adresses IP Link-Local sous Windows commencent généralement par fe80:: 
    /// </summary>
    private void ExtractIPAddresses(WiFiDirectServiceSession session, 
        out string localIPv4, out string remoteIPv4, 
        out string localIPv6, out string remoteIPv6)
    {
        localIPv4 = null;
        remoteIPv4 = null;
        localIPv6 = null;
        remoteIPv6 = null;

        try
        {
            var endpointPairs = session.GetConnectionEndpointPairs();
            if (endpointPairs == null) return;

            foreach (var pair in endpointPairs)
            {
                // Analyse de l'adresse locale
                if (pair.LocalHostName != null)
                {
                    var rawLocal = pair.LocalHostName.CanonicalName;
                    if (IPAddress.TryParse(rawLocal, out var localIp))
                    {
                        if (localIp.AddressFamily == AddressFamily.InterNetwork)
                        {
                            localIPv4 = localIp.ToString();
                        }
                        else if (localIp.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            // Vérification IPv6 Link-Local (fe80::/10)
                            if (localIp.IsIPv6LinkLocal || rawLocal.StartsWith("fe80", StringComparison.OrdinalIgnoreCase))
                            {
                                localIPv6 = rawLocal; // Conserve l'interface scope ID (%index de l'interface)
                            }
                        }
                    }
                }

                // Analyse de l'adresse distante (Peer)
                if (pair.RemoteHostName != null)
                {
                    var rawRemote = pair.RemoteHostName.CanonicalName;
                    if (IPAddress.TryParse(rawRemote, out var remoteIp))
                    {
                        if (remoteIp.AddressFamily == AddressFamily.InterNetwork)
                        {
                            remoteIPv4 = remoteIp.ToString();
                        }
                        else if (remoteIp.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            if (remoteIp.IsIPv6LinkLocal || rawRemote.StartsWith("fe80", StringComparison.OrdinalIgnoreCase))
                            {
                                remoteIPv6 = rawRemote;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erreur lors de l'extraction des IPs : {ex.Message}");
        }
    }

    #endregion

    #region Gestion globale des erreurs et exceptions

    private void HandleException(Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[WifiAwareService Error] {ex}");
        CurrentStatus = WifiAwareStatus.Error;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(WifiAwareService));
        }
    }

    #endregion

    #region Pattern de nettoyage IDisposable

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                try
                {
                    Stop();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Exception durant le Dispose : {ex.Message}");
                }
            }
            _isDisposed = true;
        }
    }

    ~WifiAwareService()
    {
        Dispose(disposing: false);
    }

    #endregion
}
