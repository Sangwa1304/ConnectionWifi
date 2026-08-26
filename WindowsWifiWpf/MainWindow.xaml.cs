using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WindowsWifiLibrary.Core.Interfaces;
using WindowsWifiLibrary.Core.Models;
using WindowsWifiLibrary.Core.Services;
using WindowsWifiLibrary.Models;
using WindowsWifiLibrary.Services;
using WindowsWifiWpf.Models;

namespace WindowsWifiWpf 
{ 
    public partial class MainWindow : Window 
    { 
        private FileTransferService _transferService;
        private CancellationTokenSource? _ctsTransfert;
        private CancellationTokenSource? _ctsWatcher;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _ctsInvitation;
        private WindowsWifiDirectService _wifiService;
        public event EventHandler<WifiDirectConnectionEventArgs>? Sended;
        private readonly List<WifiDirectPeer> Peers = [];
        public ObservableCollection<WifiDirectPeer> DiscoveredDevices { get; set; }
        public string LocalIp { get; private set; }
        public string RemoteIp { get; private set; }
        public WifiDirectPeer? Save { get; private set; }

        private readonly List<string> _pendingTransferPaths = [];
 
        // Remplacer par l'IP locale réelle générée par ta bibliothèque Wi-Fi Direct 
        //private const string LocalIp = "127.0.0.1";
        private const int LocalPort = 5050;
 
        public MainWindow()
        {
            InitializeComponent();

            _wifiService = new();
            _transferService = new();
            _wifiService.InitializeAsync().Wait();
            Init();

            DiscoveredDevices = [];
            DevicesItemsControl.ItemsSource = DiscoveredDevices;

            if (!_wifiService.IsHardwareSupported)
            {
                _wifiService.Stop();
                _wifiService = null;
                _transferService = null;
                DisableFeatures();
                return;
            }
            _wifiService.StartPublishingAsync().Wait();

            // Mock pour tester l'UI
            DiscoveredDevices.Add(new WifiDirectPeer { Name = "Glory-Phone" });
        }

        private bool Init()
        {
            if(_wifiService == null || _transferService ==null ) return false;
            _wifiService.StatusChanged += WifiService_StatusChanged;
            _wifiService.PeerDiscovered += WifiService_PeerDiscovered;
            _wifiService.PeerUpdated += WifiService_PeerUpdated;
            _wifiService.PeerRemoved += WifiService_PeerRemoved;
            _wifiService.InvitationReceived += WifiService_InvitationReceived;
            _wifiService.ConnectionEstablished += WifiService_ConnectionEstablished;
            _wifiService.ConnectionClosed += WifiService_ConnectionClosed;
            _transferService.IncomingTransferRequested += OnIncomingTransferRequested;
            return true;
        }

        private void DisableFeatures()
        {
           WindError.Visibility = Visibility.Visible;
           WindNormal.Visibility = Visibility.Collapsed;
        }
        private void EnabledFeatures()
        {
           WindError.Visibility = Visibility.Visible;
           WindNormal.Visibility = Visibility.Collapsed;
        }
            
        private void RetryPage()
        {
            _wifiService = new();
            if(!_wifiService.IsHardwareSupported)
            {   
                _wifiService.Stop();
                _wifiService = null;
                _transferService = null;
                DisableFeatures();
                return;
            }
            EnabledFeatures();
            _transferService = new();
            Init();
            _wifiService.StartPublishingAsync().Wait();
            
        }
        private void Window_DragEnter(object sender, DragEventArgs e) 
        { 
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; 
        }
 
        private void Window_Drop(object sender, DragEventArgs e) 
        { 
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                AddToSender(files);
            }
        }

        private void AddToSender(string[] files)
        {
            _pendingTransferPaths.Clear();
            _pendingTransferPaths.AddRange(from f in files where !string.IsNullOrWhiteSpace(f.Trim())&&Path.Exists(f) select f );
            DropStatusText.Text = $"{_pendingTransferPaths.Count} élément(s) prêt(s). Sélectionnez un appareil en bas.";
            Task.Run(async() => await _wifiService.StartSubscribingAsync());
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e) 
        { 
            var dialog = new OpenFileDialog{ CheckFileExists = true, Multiselect = true,Title = "Select File(s) or Folder(s) ", DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}; 
            if (dialog.ShowDialog(this) == true)  AddToSender(dialog.FileNames);
        }
        private void Retry_Button (object sender, RoutedEventArgs e) 
        { 
            RetryPage();
        }

        private async void BtnConnectAndSend_Click(object sender, RoutedEventArgs e) 
        { 
            
            if (sender is FrameworkElement element && element.Tag is WifiDirectPeer targetDevice) 
            {
                if (_pendingTransferPaths.Count == 0) 
                { 
                    MessageBox.Show("Veuillez d'abord glisser-déposer des éléments."); 
                    return;
                } 
 
                try
                {
                    ProgressText.Text = "Demande de connection en cours...";
                    _ctsInvitation = new();
                }
                catch (Exception ex)
                {
                    ProgressText.Text = $"Erreur : {ex.Message}"; 
                } 
            } 
        }

        private async void WaitConnectionEstablish(object? sender, WifiDirectConnectionEventArgs e)
        {
            Sended -= WaitConnectionEstablish;
            if(Save != null && LocalIp != null)
            {
                await StartSendAsync(new PeerDevice {Id = Save.Id,Name = Save.Name,IpAddress=RemoteIp,Port = LocalPort});
                Save = null;
            }
            
        }

        private async Task WaitConnectionEstablishAsync(WifiDirectPeer targetDevice)
        {
            Save = targetDevice;
            Sended += WaitConnectionEstablish;
            await _wifiService.ConnectToPeerAsync(targetDevice);
            //await StartSendAsync(targetDevice);
        }

        private async Task StartSendAsync(PeerDevice targetDevice)
        {
            //PeerDevice targetDevice = new PeerDevice {Id = device.Id,Name = device.Name,IpAddress = }
            ProgressText.Text = "Préparation des métadonnées...";
            var metadata = await _transferService.PrepareMetadataAsync(_pendingTransferPaths, LocalIp, LocalPort);

            ProgressText.Text = "Demande d'appairage en cours...";
            bool accepted = await _transferService.RequestPairingAsync(targetDevice, metadata, CancellationToken.None);

            ProgressText.Text = accepted ? "Transfert accepté. L'appareil distant télécharge vos fichiers." : "Demande refusée.";
        }

        private void OnIncomingTransferRequested(object? sender, TransferSessionMetadata metadata) 
        { 
            Dispatcher.Invoke(async () =>  
            { 
                var msg = $"{metadata.SenderName} veut vous envoyer {metadata.TotalFiles} fichiers ({(metadata.TotalSize / 1024 / 1024)} Mo).\nAccepter ?"; 
                if (MessageBox.Show(msg, "Réception", MessageBoxButton.YesNo) == MessageBoxResult.Yes) 
                { 
                    _ctsTransfert = new CancellationTokenSource(); 
                    string destPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Wi-Fi P2P"); 
                    Directory.CreateDirectory(destPath); 
 
                    var progress = new Progress<FileTransferProgress>(p =>  
                    { 
                        TransferProgressBar.Value = p.Percentage; 
                        ProgressText.Text = $"Réception : {p.CurrentFileName} ({p.Percentage:0.0}%)"; 
                    }); 
 
                    try 
                    { 
                        await _transferService.DownloadSessionAsync(metadata, destPath, progress, _ctsTransfert.Token); 
                        ProgressText.Text = "Transfert terminé avec succès !"; 
                    } 
                    catch (Exception ex) 
                    { 
                        ProgressText.Text = $"Erreur de téléchargement : {ex.Message}"; 
                    } 
                } 
            }); 
        } 
    private async void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e) 
    { 
        if(SenderMode())
        {
           Sended?.Invoke(sender,e);
        }
        if (!Dispatcher.CheckAccess())
        { 
            
            await Dispatcher.BeginInvoke(new Action(async () => {await ConfigureTransferEndpointAsync(e);})); 
            return;
        }
        else
        {
            await ConfigureTransferEndpointAsync(e); 
        }
    }

        private bool SenderMode()
        {
            return _pendingTransferPaths.Count == 0;
        }

        private async Task ConfigureTransferEndpointAsync(WifiDirectConnectionEventArgs e)
        {
            LocalIp = e.LocalIpAddress;
            RemoteIp = e.RemoteIpAddress;
            // Démarrage du serveur en arrière-plan
            _ = _transferService.StartListeningAsync(LocalIp, LocalPort, CancellationToken.None);
        }

        private async void WifiService_ConnectionClosed(object? sender, WifiDirectConnectionEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        { 
            //_cts
            LocalIp = null;
            RemoteIp = null;
        } 
    }
    private void WifiService_StatusChanged(object? sender, WifiDirectStatusEventArgs e)
        => Debug.WriteLine(e); //Dispatcher.Invoke(() => StatusText.Text = e.Status.ToString()); 
    
    private void WifiService_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e)  
        => Dispatcher.Invoke(() =>  DiscoveredDevices.Add(e.Peer)); 
     
    private void WifiService_PeerUpdated(object? sender, WifiDirectPeerEventArgs e)  
        => Dispatcher.Invoke(() => RefreshPeer(e.Peer)); 
     
    private void WifiService_PeerRemoved(object? sender, WifiDirectPeerEventArgs e)  
        => Dispatcher.Invoke(() => RemovePeer(e.Peer)); 

    private async void WifiService_InvitationReceived(object? sender, WifiDirectInvitationEventArgs e) 
    { 
        if (!Dispatcher.CheckAccess())
        { 
            await Dispatcher.BeginInvoke(new Action(async () => await HandleInvitationAsync(e))); 
            return; 
        } 

        await HandleInvitationAsync(e); 
    } 

    private async Task HandleInvitationAsync(WifiDirectInvitationEventArgs invitation) 
    {
        var result = MessageBox.Show(
            this, 
            $"{invitation.Request.DeviceInformation.Name} souhaite établir une connexion Wi-Fi Direct.", 
            "Demande de connexion", 
            MessageBoxButton.YesNo, 
            MessageBoxImage.Question); 

        try 
        { 
            if (result == MessageBoxResult.Yes) 
            { 
                await _wifiService.AcceptInvitationAsync(invitation.Token); 
                MessageText.Text = "Demande acceptée."; 
            } 
            else 
            { 
                await _wifiService.RejectInvitationAsync(invitation.Token); 
                MessageText.Text = "Demande refusée."; 
            } 
        } 
        catch (Exception ex) 
        { 
            MessageText.Text = $"Traitement de la demande impossible : {ex.Message}"; 
        } 
    } 

    private void RefreshPeer(WifiDirectPeer peer) 
    { 
        WifiDirectPeer[] _value = [.. from _peer in DiscoveredDevices where _peer.Id == peer.Id && _peer.Name == peer.Name select _peer];
        
        if(DiscoveredDevices.Contains(_value[0]))
        {
            DiscoveredDevices[DiscoveredDevices.IndexOf(_value[0])] = peer;
        }
        else DiscoveredDevices.Add(peer); 
    }

    private void RemovePeer(WifiDirectPeer peer) 
    {
        DiscoveredDevices.Remove(peer);
    }

    private async Task RunUiOperationAsync(Func<Task> operation) 
    { 
        try { await operation(); } 
        catch (Exception ex) { MessageText.Text = ex.Message; } 
    }
 
        protected override async void OnClosed(EventArgs e) 
        { 
            _ctsTransfert?.Cancel();
            _ctsWatcher?.Cancel();
            _cts?.Cancel();
            await _transferService.StopListeningAsync(); 
            base.OnClosed(e);
        }
    }
}