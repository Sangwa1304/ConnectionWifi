using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Windows.System.Profile;
using WindowsWifiLibrary.Models;
using WindowsWifiLibrary.Services;

namespace WindowsWifiWpf;


/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{

    private bool GetConnecteVisibility()
    {
        switch(_wifiService.CurrentStatus)
        {
            case WifiDirectStatus.Connected:
                return true;
            default:
                return false;
        }
    }
    private bool GetStartVisibility()
    {
        if (_wifiService == null) return true;
        switch (_wifiService.CurrentStatus)
        {
            case WifiDirectStatus.Closed:
                return true;
            default:
                return false;
        }
    }

    private WifiDirectPeer? _PeerConnecte = null;
    public WifiDirectPeer PeerConnecte 
    { 
        get => _PeerConnecte?? new("","");
        private set
        {
            _PeerConnecte = value;
            PropertyChanged?.Invoke(this,new(nameof(PeerConnecte)));
        }
    }
    public Visibility ConnectedVisibilityOn { get => GetConnecteVisibility() ? Visibility.Visible : Visibility.Collapsed; }
    public Visibility ConnectedVisibilityOff { get => !GetConnecteVisibility() ? Visibility.Visible : Visibility.Collapsed; }
    public Visibility StopB { get => !GetStartVisibility() ? Visibility.Visible : Visibility.Collapsed; }
    public Visibility StartB { get => GetStartVisibility() ? Visibility.Visible : Visibility.Collapsed; }

    public ObservableCollection<WifiDirectPeer> PeersDiscoved { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        _ = Task.Run(() =>
        {
            _wifiService = new();

            _wifiService.StatusChanged += WifiService_StatusChanged;
            _wifiService.PeerDiscovered += WifiService_PeerDiscovered;
            _wifiService.PeerUpdated += WifiService_PeerUpdated;
            _wifiService.PeerRemoved += WifiService_PeerRemoved;
            _wifiService.InvitationReceived += WifiService_InvitationReceived;
            _wifiService.ConnectionEstablished += WifiService_ConnectionEstablished;
            _wifiService.ConnectionClosed += WifiService_ConnectionClosed;
        });
        DataContext = this;

    }

    private WindowsWifiDirectService _wifiService;

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        PeersDiscoved.Clear();
        await RunUiOperationAsync(() => _wifiService.StartPublisherSubscribingAsync());

    }

    public void OnPropertyChanged(string nameOfProperty)
    {
        PropertyChanged?.Invoke(this, new(nameOfProperty));
    }

    private void Messager(Combo message)
    {
        MessageText.Text = message.Message;
        StatusText.Text = message.Statut(StatusText.Text);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _wifiService?.Stop();
        MessageText.Text = "Services arrêtés.";
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false, DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
        
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {

    }

    private async void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            PeerConnecte = GetPeer(e.Id);
            await Dispatcher.BeginInvoke(new Action(async () => { await ConfigureTransferEndpointAsync(e);}));
            return;
        }
        else
        {
            await ConfigureTransferEndpointAsync(e);
        }
    }

    private WifiDirectPeer GetPeer(string id)
    {
        foreach (var device in PeersDiscoved)
        {
            if (device.Id == id)
            {
                return device;
            }
        }
        throw new Exception("le device est pas disponible");
    }
    private async void WifiService_ConnectionClosed(object? sender, WifiDirectPeerEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            PeerConnecte = null;
        }
    }

    private async Task ConfigureTransferEndpointAsync(WifiDirectConnectionEventArgs e)
    {

    }

    private void WifiService_StatusChanged(object? sender, WifiDirectStatusEventArgs e)
    {
        if (e.Status == WifiDirectStatus.Closed) PeersDiscoved.Clear();
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = e.Status.ToString();
            OnPropertyChanged(nameof(StartB));
            OnPropertyChanged(nameof(StopB));
        });
    }
    
    private void WifiService_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e)  
        => Dispatcher.Invoke(() => AddPeer(e.Peer));

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

    private void AddPeer(WifiDirectPeer peer)
    {
        if (PeersDiscoved.Contains(peer)) PeersDiscoved.Remove(peer);
        PeersDiscoved.Add(peer);
    }

    private void RefreshPeer(WifiDirectPeer peer) 
    {
        if (PeersDiscoved.Contains(peer))
        {
            PeersDiscoved[PeersDiscoved.IndexOf(peer)] = peer;
        }
    } 

    private void RemovePeer(WifiDirectPeer peer) 
    {
        
        if(PeersDiscoved.Contains(peer))
        {
            PeersDiscoved.Remove(peer);
        }
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
        _wifiService.Stop(); 
        _wifiService.Dispose(); 
        base.OnClosed(e); 
    }

    private void Retry_Button(object sender, RoutedEventArgs e)
    {
        StartButton_Click(sender, e);
    }
}

internal class Combo(string message, string? status = null,Brush? color = null )
{
    public string Message { get; } = message;
    private string? _Status = status;
    public Brush Elipse = color?? Brushes.White;

    internal string Statut(string status)
    {
        return _Status?? status;
    }
}