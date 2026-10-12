using LibraryMI.Models;
using Microsoft.Win32;
using ServiceTcpChared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using WindowsWifiLibrary.Models;
using WindowsWifiLibrary.Services;

namespace WindowsWifiWpf.ViewModels;
public partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public class ServerClient
    {
        public TcpClientStreaming? ClientInstance { get; private set; }
        public TcpServerStreaming? ServerInstance { get; private set; }
        public event Func<InfoMetas,Task<bool>>?  TransfertRequest;
        public event Action? CounterServerChange;
        public event Action? CounterClientChange;
        public event Action? FileServerChange;
        public event Action? FileClientChange;

        internal void Stop()
        {
            ServerInstance?.Stop();
            ClientInstance?.Stop();
        }

        private bool ServerClient_FilesComming(IEnumerable<InfoMeta> arg)
        {
            return TransfertRequest?.Invoke(new(arg)).GetAwaiter().GetResult()??false;
        }

        private void ServerClient_CounterChange()
        {
            CounterServerChange?.Invoke();
        }
        private void Client_CounterChange()
        {
            CounterClientChange?.Invoke();
        }

        private void ServerClient_FileChange()
        {
            FileClientChange?.Invoke();
        }

        private void ServerInstance_FileChange()
        {
            FileServerChange?.Invoke();
        }

        internal async Task SendFilesAsync(IEnumerable<FileI> pendingFiles, CancellationToken token)
        {
            List<string> f = [.. from e in pendingFiles select e.PathFile];

            ServerInstance?.Add(f);
        }

        internal async Task Start(WifiDirectConnectionEventArgs e, CancellationToken token)
        {
            Stop();
            ServerInstance = new(e.ServerMode);
            ClientInstance = new(e.RemoteIpAddress, e.ServerMode);

            ServerInstance?.FileChange += ServerInstance_FileChange;
            ClientInstance?.FileChange += ServerClient_FileChange;
            ServerInstance?.CounterChange += ServerClient_CounterChange;
            ClientInstance?.CounterChange += Client_CounterChange;
            ClientInstance?.FilesComming += ServerClient_FilesComming;
            ServerInstance?.FilesComming += ServerClient_FilesComming;
            
            if (e.ServerMode)
            {
                _ = ServerInstance?.StartConnection(token);
                _ = ClientInstance?.StartConnection(token);
            }
            else
            {
                _ = ClientInstance?.StartConnection(token);
                _ = ServerInstance?.StartConnection(token);
            }
        }
    }
    private readonly WindowsWifiDirectService _wifiService;
    #region : Server and Client 
    private readonly ServerClient ServerClientInstance;

    public string FileClient { get => ServerClientInstance.ClientInstance?.FileReceivedOrSend ?? ""; }
    public string FilServer { get => ServerClientInstance.ServerInstance?.FileReceivedOrSend ?? ""; }
    public float CounterServer { get => ServerClientInstance.ServerInstance?.Percentage ?? 0; }
    public float CounterClient { get => ServerClientInstance.ClientInstance?.Percentage ?? 0; }
    private void CounterClientChange() => OnPropertyChanged(nameof(CounterClient));
    private void CounterServerChange() => OnPropertyChanged(nameof(CounterServer));
    private void FileClientChange() => OnPropertyChanged(nameof(FileClient));
    private void FileServerChange() => OnPropertyChanged(nameof(FilServer));
    public Visibility ConnectedVisibilityOn { get => GetConnecteVisibility() ? Visibility.Visible : Visibility.Collapsed; }
    public Visibility ConnectedVisibilityOff { get => !GetConnecteVisibility() ? Visibility.Visible : Visibility.Collapsed; }
    public Visibility StopB { get => !GetStartVisibility() ? Visibility.Visible : Visibility.Collapsed; }
    public Visibility StartB { get => GetStartVisibility() ? Visibility.Visible : Visibility.Collapsed; }

    private bool GetConnecteVisibility()
    {

        return _wifiService?.CurrentStatus switch
        {
            WifiDirectStatus.Connected => true,
            _ => false,
        };
    }
    private bool GetStartVisibility()
    {
        if (_wifiService == null) return true;
        return _wifiService.CurrentStatus switch
        {
            WifiDirectStatus.Closed => true,
            _ => false,
        };
    }
    
    #endregion

    private string _selectedPeerName = "Aucun appareil sélectionné";
    private string _dropStatusText = "Aucun élément sélectionné";
    private string _messageText = string.Empty;
    private string _progressText = "En attente...";
    private string _downloadFolder = GetDefaultDownloadFolder();
    private double _transferProgress;
    private InfoMetas? _incomingTransferSession;
    private bool _isFileOverDropZone;

    public string SelectedPeerName { get => _selectedPeerName; set { _selectedPeerName = value; OnPropertyChanged(nameof(SelectedPeerName)); } }
    public string DropStatusText { get => _dropStatusText; set { _dropStatusText = value; OnPropertyChanged(nameof(DropStatusText)); } }
    public string MessageText { get => _messageText; set { _messageText = value; OnPropertyChanged(nameof(MessageText)); } }
    public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(nameof(ProgressText)); } }
    public string CurrentStateDisplayText { get => _wifiService.CurrentStatus.ToString(); }
    public string DownloadFolder
    {
        get => _downloadFolder;
        set
        {
            _downloadFolder = string.IsNullOrWhiteSpace(value) ? GetDefaultDownloadFolder() : value;
            OnPropertyChanged(nameof(DownloadFolder));
            OnPropertyChanged(nameof(DownloadFolderDisplay));
        }
    }
    public string DownloadFolderDisplay => string.IsNullOrWhiteSpace(DownloadFolder) ? "Dossier par défaut" : DownloadFolder;
    public double TransferProgress { get => _transferProgress; set { _transferProgress = value; OnPropertyChanged(nameof(TransferProgress)); } }

    public bool IsFileOverDropZone
    {
        get => _isFileOverDropZone;
        set { _isFileOverDropZone = value; OnPropertyChanged(nameof(IsFileOverDropZone)); }
    }

    public ObservableCollection<WifiDirectPeer> DiscoveredDevices { get; } = [];
    public ObservableCollection<FileI> PendingFiles { get; } = [];
    public ObservableCollection<TransferHistoryEntry> RecentTransfers { get; } = [];
    
    public WifiDirectPeer? PeerConnecte { get; private set; }
    public bool HasRecentTransfers => RecentTransfers.Count > 0;
    public bool HasPendingFiles => PendingFiles.Count > 0;
    public bool NoPendingFiles => !HasPendingFiles;
    public bool NoHardwardSuported 
    { 
        get => _wifiService.CurrentStatus == WifiDirectStatus.NoArdwareSupported;
    }

    public Visibility PageReloadVisible
    {
        get => NoHardwardSuported ? Visibility.Visible : Visibility.Collapsed;
    }
    public Visibility PageReloadNoVisible
    {
        get => NoHardwardSuported ? Visibility.Collapsed : Visibility.Visible;
    }
    public InfoMetas? IncomingTransferSession
    {
        get => _incomingTransferSession;
        set
        {
            _incomingTransferSession = value;
            OnPropertyChanged(nameof(IncomingTransferSession));
            OnPropertyChanged(nameof(IsTransferRequestVisible));
            OnPropertyChanged(nameof(TransferRequestSummary));
        }
    }

    public bool IsTransferRequestVisible => IncomingTransferSession != null;
    public string TransferRequestSummary => IncomingTransferSession == null
        ? string.Empty
        : $"{PeerConnecte?.Name} souhaite envoyer {IncomingTransferSession.Length} élément(s) ({FormatSize(IncomingTransferSession.Length)}).";


    private CancellationTokenSource? _ctsWatcher;
    private CancellationTokenSource? _ctsTransfert;

    public bool Accept { get; private set; }

    private CancellationTokenSource? _ctsConnection;

    public ICommand BrowseCommand { get; }
    public ICommand ChangeFolderCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public ICommand DeleteItemCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectAndSendCommand { get; }
    public ICommand AcceptTransferCommand { get; }
    public ICommand RejectTransferCommand { get; }
    public ICommand CancelTransferCommand { get; }
    public ICommand OpenDownloadFolderCommand { get; }
    public ICommand ClearTransferHistoryCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public MainViewModel()
    {
        _wifiService = new WindowsWifiDirectService();
        ServerClientInstance = new();
        ServerClientInstance.TransfertRequest += ServerClientInstance_TransfertRequest;
        // Synchronisation inter-threads sécurisée pour les collections de l'interface graphique
        BindingOperations.EnableCollectionSynchronization(DiscoveredDevices, new object());
        BindingOperations.EnableCollectionSynchronization(PendingFiles, new object());
        BindingOperations.EnableCollectionSynchronization(RecentTransfers, new object());

        BrowseCommand = new RelayCommand(async _ => await ExecuteBrowseAsync());
        ChangeFolderCommand = new RelayCommand(_ => ExecuteChangeFolder());
        RetryCommand = new RelayCommand(_ => RetrySearch());
        ReloadCommand = new RelayCommand(_ => ReloadPage());
        ScanCommand = new RelayCommand(_ => StartDiscovery());
        DeleteAllCommand = new RelayCommand(_ => DeleteAll());
        DeleteItemCommand = new RelayCommand(param => DeleteItem(param as FileI), param => param is FileI);
        ConnectAndSendCommand = new RelayCommand(async param => await ConnectAndSendAsync(param as WifiDirectPeer), param => param is WifiDirectPeer);
        ConnectCommand = new RelayCommand(async param => await ConnectAsync(param as WifiDirectPeer), param => param is WifiDirectPeer);
        SendCommand = new RelayCommand(async param => await SendAsync(param as WifiDirectPeer), param => param is WifiDirectPeer);
        DisconnectCommand = new RelayCommand(async param => await DisconnectAsync(param as WifiDirectPeer), param => param is WifiDirectPeer);
        AcceptTransferCommand = new RelayCommand(async _ => await AcceptIncomingTransferAsync(), _ => IncomingTransferSession != null);
        RejectTransferCommand = new RelayCommand(_ => RejectIncomingTransfer(), _ => IncomingTransferSession != null);
        CancelTransferCommand = new RelayCommand(_ => CancelActiveTransfert());
        OpenDownloadFolderCommand = new RelayCommand(_ => OpenDownloadFolder(), _ => !string.IsNullOrWhiteSpace(DownloadFolder));
        ClearTransferHistoryCommand = new RelayCommand(_ => ClearTransferHistory(), _ => RecentTransfers.Count > 0);

        PendingFiles.CollectionChanged += (_, __) => { OnPropertyChanged(nameof(HasPendingFiles)); OnPropertyChanged(nameof(NoPendingFiles)); UpdateDropStatus(); };
        RecentTransfers.CollectionChanged += (_, __) => { OnPropertyChanged(nameof(HasRecentTransfers)); ((RelayCommand)ClearTransferHistoryCommand).RaiseCanExecuteChanged(); };

        WireEvents();
        _ = InitializeAsync();
    }

    private async Task<bool> ServerClientInstance_TransfertRequest(InfoMetas arg)
    {
        Accept = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            IncomingTransferSession = arg;
            ProgressText = $"Réception de {arg.Metas.Count()} élément(s)";
        });
        bool response = false;
        while(!Accept && IncomingTransferSession != null)
        {
            await Task.Delay(3000);
        }
        if(Accept)response= true;
        Accept = false;
        return response;
    }

    private async Task DisconnectAsync(WifiDirectPeer? wifiDirectPeer)
    {
        _wifiService.DisconnectToPeer( ref wifiDirectPeer,default);
        PeerConnecte = wifiDirectPeer;
    }

    private void WireEvents()
    {
        ServerClientInstance.CounterClientChange += ClientInstance_CounterChanged;
        ServerClientInstance.CounterServerChange += ServerInstance_CounterChanged;
        _wifiService.StatusChanged += WifiService_StatusChanged;
        _wifiService.PeerDiscovered += WifiService_PeerDiscovered;
        _wifiService.PeerUpdated += WifiService_PeerUpdated;
        _wifiService.PeerRemoved += WifiService_PeerRemoved;
        _wifiService.InvitationReceived += WifiService_InvitationReceived;
        _wifiService.ConnectionEstablished += WifiService_ConnectionEstablished;
        _wifiService.ConnectionClosed += WifiService_ConnectionClosed;
    }

    private void ServerInstance_CounterChanged()
    {
        CounterServerChange();
    }

    private void ClientInstance_CounterChanged()
    {
        CounterClientChange();
    }

    private async Task InitializeAsync(bool reload = false)
    {


        _ctsWatcher ??= new();
        if (NoHardwardSuported)
        {
            if(reload)
            {
                await _wifiService.InitializeAsync(_ctsWatcher.Token);
                await InitializeAsync();
            }
            return;
        }
        _ = _wifiService.StartPublisherSubscribingAsync(_ctsWatcher.Token);
        
    }

    private void StartDiscovery()
    {
        _ctsWatcher?.Cancel();
        DiscoveredDevices.Clear();
        _ctsWatcher = new CancellationTokenSource();
        _=_wifiService.StartPublisherSubscribingAsync(_ctsWatcher.Token);
    }
    private async Task ExecuteBrowseAsync()
    {
        OpenFileDialog dialog = new() { CheckFileExists = true, Multiselect = true, Title = "Sélectionner des éléments" };
        if (dialog.ShowDialog() is true) AddFiles(dialog.FileNames);
        await Task.CompletedTask;
    }

    private void ExecuteChangeFolder()
    {
        OpenFolderDialog dialog = new() { Title = "Choisir le dossier de réception", InitialDirectory = DownloadFolder };
        if (dialog.ShowDialog() is true)
        {
            DownloadFolder = dialog.FolderName;
        }
    }

    public void AddFiles(IEnumerable<string>? files)
    {
        if (files == null) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var added = 0;
            foreach (var rawPath in files.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var candidate = Path.GetFullPath(rawPath.Trim());
                if (!PendingFiles.Any(item => string.Equals(item.PathFile, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    PendingFiles.Add(new FileI(candidate));
                    added++;
                }
            }
        });
    }

    private async Task ConnectAndSendAsync(WifiDirectPeer? target)
    {
        await ConnectAsync(target);
        await SendAsync(target);
    }
    private async Task ConnectAsync(WifiDirectPeer? target)
    {
        if (target == null) return;
        try
        {   
            _ctsConnection?.Cancel();
            SelectedPeerName = target.Name;
            PeerConnecte = target;
            _ctsConnection = new CancellationTokenSource();
            await _wifiService.ConnectToPeerAsync(target, _ctsConnection.Token);
        }
        catch (Exception ex)
        {}
    }
    private async Task SendAsync(WifiDirectPeer? target)
    {
        if (target == null) return;
        if (PendingFiles.Count == 0)
        {
            MessageBox.Show("Veuillez d'abord glisser-déposer des éléments à envoyer.", "Liste vide", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        else if(_wifiService.IsConnectToPeer(target))
        {
            if (_ctsTransfert == null || !_ctsTransfert.IsCancellationRequested) _ctsTransfert = new();
            _=ServerClientInstance.SendFilesAsync(PendingFiles,_ctsTransfert.Token);
        }
        else
        {
            MessageBox.Show("Veuillez d'abord vous connecter.", "Appareil non connecter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            _ctsTransfert?.Cancel();
            await Task.Delay(100);
            _ctsTransfert = new CancellationTokenSource();
            try
            {
                await ServerClientInstance.Start(e,_ctsTransfert.Token);
            }
            catch (Exception)
            {
                return;
            }

        });
    }

    private async Task AcceptIncomingTransferAsync()
    {
        if (IncomingTransferSession == null) return;
        Accept = true;
        AppendTransferHistory(PeerConnecte?.Name ?? "Distant", "Réception 📥", "debuté", DownloadFolder, IncomingTransferSession.Length);
    }
    
    private void RejectIncomingTransfer()
    {
        if (IncomingTransferSession == null) return;

        AppendTransferHistory(PeerConnecte?.Name?? "Distant", "Réception 📥", "Refusé", DownloadFolder, IncomingTransferSession.Length);
        ClearTransferRequest();
    }

    private void CancelActiveTransfert()
    {
        _ctsTransfert?.Cancel();
        ClearTransferRequest();
    }

    private void ResetWorkflowAfterTimeout(string endMessage)
    {
        Task.Run(async () =>
        {
            await Task.Delay(4000);
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                PendingFiles.Clear();
                TransferProgress = 0;
                ProgressText = "En attente...";
                IncomingTransferSession = null;
            });
        });
    }

    private void ClearTransferRequest() => IncomingTransferSession = null;
    private void DeleteAll() { PendingFiles.Clear(); }
    private void DeleteItem(FileI? file) { if (file != null) PendingFiles.Remove(file);}
    private void OpenDownloadFolder() => Process.Start(new ProcessStartInfo { FileName = DownloadFolder, UseShellExecute = true });
    private void ClearTransferHistory() => RecentTransfers.Clear();
    private void ReloadPage() => _=InitializeAsync(true);
    private void RetrySearch() { _wifiService.Stop(); StartDiscovery(); }
    private void UpdateDropStatus() => DropStatusText = PendingFiles.Count == 0 ? "Aucun élément" : $"{PendingFiles.Count} prêt(s).";
    private void WifiService_StatusChanged(object? sender, WifiDirectStatusEventArgs e) 
        => 
        Application.Current.Dispatcher.Invoke(() => 
        {

            OnPropertyChanged(nameof(NoHardwardSuported));
            OnPropertyChanged(nameof(PageReloadNoVisible));
            OnPropertyChanged(nameof(PageReloadVisible));
            OnPropertyChanged(nameof(StartB));
            OnPropertyChanged(nameof(StopB));
            OnPropertyChanged(nameof(CurrentStateDisplayText));
            OnPropertyChanged(nameof(ConnectedVisibilityOff));
            OnPropertyChanged(nameof(ConnectedVisibilityOn));/*MessageText = $"Statut matériel : {e.Status}"*/; 
        });
    private void WifiService_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e) => Application.Current.Dispatcher.Invoke(() => { if (!DiscoveredDevices.Contains(e.Peer)) DiscoveredDevices.Add(e.Peer); });
    
    private void WifiService_PeerUpdated(object? sender, WifiDirectPeerEventArgs e) 
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = DiscoveredDevices.FirstOrDefault(p => p.Id == e.Peer.Id);
            if (existing != null) DiscoveredDevices[DiscoveredDevices.IndexOf(existing)] = e.Peer;
            else DiscoveredDevices.Add(e.Peer);
        });
    }

    private void WifiService_PeerRemoved(object? sender, WifiDirectPeerEventArgs e) 
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = DiscoveredDevices.FirstOrDefault(p => p.Id == e.Peer.Id);
            if (existing != null) DiscoveredDevices.Remove(existing);
        });
    }

    private void WifiService_InvitationReceived(object? sender, WifiDirectInvitationEventArgs e) 
    {
        Application.Current.Dispatcher.Invoke(async () => 
        {
            var result = MessageBox.Show($"{e.Request.DeviceInformation.Name} souhaite s'appairer en Wi-Fi Direct. Autoriser ?", "Appairage entrant", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes) await _wifiService.AcceptInvitationAsync(e.Token);
            else await _wifiService.RejectInvitationAsync(e.Token);
        });
    }

    private void WifiService_ConnectionClosed(object? sender, WifiDirectPeerEventArgs e)
    {

        ServerClientInstance.Stop();
    }
    
    private void AppendTransferHistory(string user, string type, string status, string path, long size)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RecentTransfers.Insert(0, new TransferHistoryEntry { Name = user, Type = type, Status = status, Path = path, Size = size, Timestamp = DateTime.Now });
        });
    }

    private static string GetDefaultDownloadFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ShareBoost");

    private static string FormatSize(long size)
    {
        if (size < 1024) return $"{size} o";
        if (size < 1024 * 1024) return $"{size / 1024.0:0.##} Ko";
        if (size < 1024 * 1024 * 1024) return $"{size / (1024.0 * 1024):0.##} Mo";
        return $"{size / (1024.0 * 1024 * 1024):0.##} Go";
    }

    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _ctsWatcher?.Cancel();
        _ctsTransfert?.Cancel();
        _ctsConnection?.Cancel();
        _wifiService.StatusChanged -= WifiService_StatusChanged;
        _wifiService.PeerDiscovered -= WifiService_PeerDiscovered;
        _wifiService.PeerUpdated -= WifiService_PeerUpdated;
        _wifiService.PeerRemoved -= WifiService_PeerRemoved;
        _wifiService.InvitationReceived -= WifiService_InvitationReceived;
        _wifiService.ConnectionEstablished -= WifiService_ConnectionEstablished;
        _wifiService.ConnectionClosed -= WifiService_ConnectionClosed;

        ServerClientInstance.Stop();
        _wifiService.Stop();
        _ctsWatcher?.Dispose();
        _ctsTransfert?.Dispose();
        _ctsConnection?.Dispose();
    }

    private class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        private readonly Action<object?> _execute = execute;
        private readonly Predicate<object?>? _canExecute = canExecute;
        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}