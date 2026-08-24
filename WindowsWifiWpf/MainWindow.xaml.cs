<<<<<<< Updated upstream
﻿using Microsoft.Win32;
using System.Windows;
using WindowsWifiLibrary.Wifi;
=======
﻿using Microsoft.Win32; 
using System; 
using System.Linq; 
using System.Threading; 
using System.Threading.Tasks; 
using System.Windows; 
using WindowsWifiLibrary.Interfaces; 
using WindowsWifiLibrary.Models; 
using WindowsWifiLibrary.Services; 
>>>>>>> Stashed changes
using WindowsWifiWpf.Transfer;
using WindowsWifiWpf.Transfer.Models;
using WindowsWifiWpf.Transfer.Services;

namespace WindowsWifiWpf; 

<<<<<<< Updated upstream
/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    
    private readonly WifiDirectService _wifiService = new();
    private readonly FileTransferService _transferService = new();
    private WifiDirectPeer? _selectedPeer;
    private FileTransferEndpoint? _remoteEndpoint;
    private CancellationTokenSource? _transferCancellation;
=======
public partial class MainWindow : Window 
{ 
    private readonly WindowsWifiDirectService _wifiService = new(); 
    private readonly FileTransferService _transferService = new(); 
    private WifiDirectPeer? _selectedPeer; 
    private FileTransferEndpoint? _remoteEndpoint; 
    private CancellationTokenSource? _transferCancellation; 
>>>>>>> Stashed changes

    public MainWindow() 
    { 
        InitializeComponent(); 
        _wifiService.StatusChanged += WifiService_StatusChanged; 
        _wifiService.PeerDiscovered += WifiService_PeerDiscovered; 
        _wifiService.PeerUpdated += WifiService_PeerUpdated; 
        _wifiService.PeerRemoved += WifiService_PeerRemoved; 
        _wifiService.InvitationReceived += WifiService_InvitationReceived; 
        _wifiService.ConnectionEstablished += WifiService_ConnectionEstablished; 
        PeersList.SelectionChanged += (_, _) => _selectedPeer = PeersList.SelectedItem as WifiDirectPeer; 
    }

    private async void InitializeButton_Click(object sender, RoutedEventArgs e) 
    { 
        await RunUiOperationAsync(async () => 
        { 
            var ready = await _wifiService.InitializeAsync(); 
            MessageText.Text = ready ? "Wi-Fi Direct est disponible." : "Wi-Fi Direct n'est pasdisponible."; 
        }); 
    } 

    private async void PublishButton_Click(object sender, RoutedEventArgs e) 
    { 
        await RunUiOperationAsync(() => _wifiService.StartPublishingAsync("ConnectionWifi", Environment.MachineName)); 
    } 

    private async void SubscribeButton_Click(object sender, RoutedEventArgs e) 
    { 
        await RunUiOperationAsync(() => _wifiService.StartSubscribingAsync("ConnectionWifi")); 
    } 

    private void StopButton_Click(object sender, RoutedEventArgs e) 
    { 
        _transferCancellation?.Cancel(); 
        _wifiService.Stop(); 
        _ = _transferService.StopAsync(); 
        MessageText.Text = "Services arrêtés."; 
    } 

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) 
    { 
        if (_selectedPeer is null) 
        { 
            MessageText.Text = "Sélectionnez un appareil."; 
            return; 
        } 
        await RunUiOperationAsync(() => _wifiService.ConnectToPeerAsync(_selectedPeer)); 
    } 

    private void BrowseButton_Click(object sender, RoutedEventArgs e) 
    { 
        var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false }; 
        if (dialog.ShowDialog(this) == true) FilePathTextBox.Text = dialog.FileName; 
    } 

    private async void SendButton_Click(object sender, RoutedEventArgs e) 
    { 
        if (_remoteEndpoint is null) 
        { 
            MessageText.Text = "Aucune connexion active."; 
            return; 
        } 
        if (string.IsNullOrWhiteSpace(FilePathTextBox.Text)) 
        { 
            MessageText.Text = "Sélectionnez un fichier à envoyer."; 
            return; 
        } 

        _transferCancellation?.Cancel(); 
        _transferCancellation = new CancellationTokenSource(); 
        TransferProgressBar.Value = 0; 
        try 
        { 
            var progress = new Progress<FileTransferProgress>(value => 
            { 
                TransferProgressBar.Value = value.Percentage; 
                TransferText.Text = $"{value.Percentage:0.0}% ({FormatBytes(value.BytesTransferred)} / {FormatBytes(value.TotalBytes)})";}); 
            await _transferService.SendFileAsync(_remoteEndpoint, FilePathTextBox.Text, progress, _transferCancellation.Token); 
            MessageText.Text = "Fichier envoyé avec succès."; 
        } 
        catch (OperationCanceledException) { MessageText.Text = "Transfert annulé."; } 
        catch (Exception ex) { MessageText.Text = $"Échec du transfert : {ex.Message}"; } 
    } 

    private async void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e) 
    { 
        if (!Dispatcher.CheckAccess()) 
        { 
            await Dispatcher.BeginInvoke(new Action(async () => await ConfigureTransferEndpointAsync(e))); 
            return; 
        } 

        await ConfigureTransferEndpointAsync(e); 
    } 

<<<<<<< Updated upstream
    private async Task ConfigureTransferEndpointAsync(WifiDirectConnectionEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.LocalIPv4) || string.IsNullOrWhiteSpace(e.RemoteIPv4))
        {
            MessageText.Text = "Connexion établie, mais aucune adresse IPv4 exploitable n'a été trouvée.";
            return;
        }

        try
        {
            var storage = ExpandPath(StoragePathTextBox.Text);
            var endpoint = await _transferService.StartAsync(e.LocalIPv4, storage, e.SessionId);
            _remoteEndpoint = new FileTransferEndpoint(new Uri($"http://{e.RemoteIPv4}:{FileTransferService.DefaultPort}/"), e.SessionId);
            MessageText.Text = $"Partage prêt avec {e.RemoteIPv4}.";
        }
        catch (Exception ex) { MessageText.Text = $"Impossible de démarrer le partage : {ex.Message}"; }
    }

    private void WifiService_StatusChanged(object? sender, WifiDirectStatus e)
     => Dispatcher.Invoke(() => StatusText.Text = e.ToString());
    private void WifiService_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e) => Dispatcher.Invoke(() => PeersList.Items.Add(e.Peer));
    private void WifiService_PeerUpdated(object? sender, WifiDirectPeerEventArgs e) => Dispatcher.Invoke(() => RefreshPeer(e.Peer));
    private void WifiService_PeerRemoved(object? sender, WifiDirectPeerEventArgs e) => Dispatcher.Invoke(() => RemovePeer(e.Peer));
=======
    private async Task ConfigureTransferEndpointAsync(WifiDirectConnectionEventArgs e) 
    { 
        if (string.IsNullOrWhiteSpace(e.LocalIpAddress) || string.IsNullOrWhiteSpace(e.RemoteIpAddress)) 
        { 
            MessageText.Text = "Connexion établie, mais aucune adresse IPv4 exploitable n'a été trouvée."; 
            return; 
        } 

        try 
        { 
            var storage = ExpandPath(StoragePathTextBox.Text); 
            await _transferService.StartAsync(e.LocalIpAddress, storage); 
            _remoteEndpoint = new FileTransferEndpoint(new Uri($"http://{e.RemoteIpAddress}:{FileTransferService.DefaultPort}/")); 
            MessageText.Text = $"Partage prêt avec {e.RemoteIpAddress}."; 
        } 
        catch (Exception ex) { MessageText.Text = $"Impossible de démarrer le partage : {ex.Message}"; } 
    } 

    private void WifiService_StatusChanged(object? sender, WifiDirectStatusEventArgs e) 
        => Dispatcher.Invoke(() => StatusText.Text = e.Status.ToString()); 
     
    private void WifiService_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e)  
        => Dispatcher.Invoke(() => PeersList.Items.Add(e.Peer)); 
     
    private void WifiService_PeerUpdated(object? sender, WifiDirectPeerEventArgs e)  
        => Dispatcher.Invoke(() => RefreshPeer(e.Peer)); 
     
    private void WifiService_PeerRemoved(object? sender, WifiDirectPeerEventArgs e)  
        => Dispatcher.Invoke(() => RemovePeer(e.Peer)); 
>>>>>>> Stashed changes

    private async void WifiService_InvitationReceived(object? sender, WifiDirectInvitationEventArgs e) 
    { 
        if (!Dispatcher.CheckAccess()) 
        { 
            await Dispatcher.BeginInvoke(new Action(async () => await HandleInvitationAsync(e))); 
            return; 
        } 

        await HandleInvitationAsync(e); 
    } 

<<<<<<< Updated upstream
    private async Task HandleInvitationAsync(WifiDirectInvitationEventArgs invitation)
    {
        var result = MessageBox.Show(
            this,
            $"{invitation.SenderName} souhaite établir une connexion Wi-Fi Direct.",
            "Demande de connexion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
=======
    private async Task HandleInvitationAsync(WifiDirectInvitationEventArgs invitation) 
    { 
        var result = MessageBox.Show( 
            this, 
            $"{invitation.Request.DeviceInformation.Name} souhaite établir une connexion Wi-Fi Direct.", 
            "Demande de connexion", 
            MessageBoxButton.YesNo, 
            MessageBoxImage.Question); 
>>>>>>> Stashed changes

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
        var index = Enumerable.Range(0, PeersList.Items.Count) 
            .FirstOrDefault(i => (PeersList.Items[i] as WifiDirectPeer)?.Id == peer.Id, -1); 
        if (index >= 0) PeersList.Items[index] = peer; 
        else PeersList.Items.Add(peer); 
    } 

    private void RemovePeer(WifiDirectPeer peer) 
    { 
        var item = PeersList.Items.Cast<WifiDirectPeer>().FirstOrDefault(candidate => candidate.Id == peer.Id); 
        if (item is not null) PeersList.Items.Remove(item); 
        if (_selectedPeer?.Id == peer.Id) _selectedPeer = null; 
    } 

    private async Task RunUiOperationAsync(Func<Task> operation) 
    { 
        try { await operation(); } 
        catch (Exception ex) { MessageText.Text = ex.Message; } 
    } 

    private static string ExpandPath(string path) => Environment.ExpandEnvironmentVariables(path); 
    private static string FormatBytes(long bytes) => bytes switch 
    { 
        >= 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.0} Go", 
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} Mo", 
        >= 1024 => $"{bytes / 1024d:0.0} Ko", 
        _ => $"{bytes} o" 
    }; 

    protected override async void OnClosed(EventArgs e) 
    { 
        _transferCancellation?.Cancel(); 
        _wifiService.Stop(); 
        await _transferService.DisposeAsync(); 
        _wifiService.Dispose(); 
        base.OnClosed(e); 
    } 
} 
