using LibraryMI.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ServiceTcpChared;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using WindowsWifiLibrary.Models;
using WindowsWifiLibrary.Services;

namespace ShareBoost.UI.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private readonly IntPtr _windowHandle;

    public class ServerClient
    {
        public TcpClientStreaming? ClientInstance { get; private set; }
        public TcpServerStreaming? ServerInstance { get; private set; }
        public event Func<InfoMetas, Task<bool>>? TransfertRequest;
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
            return TransfertRequest?.Invoke(new(arg)).GetAwaiter().GetResult() ?? false;
        }

        private void ServerClient_CounterChange() => CounterServerChange?.Invoke();
        private void Client_CounterChange() => CounterClientChange?.Invoke();
        private void ServerClient_FileChange() => FileClientChange?.Invoke();
        private void ServerInstance_FileChange() => FileServerChange?.Invoke();

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

            ServerInstance.FileChange += ServerInstance_FileChange;
            ClientInstance.FileChange += ServerClient_FileChange;
            ServerInstance.CounterChange += ServerClient_CounterChange;
            ClientInstance.CounterChange += Client_CounterChange;
            ClientInstance.FilesComming += ServerClient_FilesComming;
            ServerInstance.FilesComming += ServerClient_FilesComming;

            if (e.ServerMode)
            {
                _ = ServerInstance.StartConnection(token);
                _ = ClientInstance.StartConnection(token);
            }
            else
            {
                _ = ClientInstance.StartConnection(token);
                _ = ServerInstance.StartConnection(token);
            }
        }
    }

    private readonly WindowsWifiDirectService _wifiService;
    private readonly ServerClient ServerClientInstance;

    public string FileClient => ServerClientInstance.ClientInstance?.FileReceivedOrSend ?? "";
    public string FilServer => ServerClientInstance.ServerInstance?.FileReceivedOrSend ?? "";
    public float CounterServer => ServerClientInstance.ServerInstance?.Percentage ?? 0;
    public float CounterClient => ServerClientInstance.ClientInstance?.Percentage ?? 0;

    private void CounterClientChange() => OnPropertyChanged(nameof(CounterClient));
    private void CounterServerChange() => OnPropertyChanged(nameof(CounterServer));
    private void FileClientChange() => OnPropertyChanged(nameof(FileClient));
    private void FileServerChange() => OnPropertyChanged(nameof(FilServer));

    public Visibility ConnectedVisibilityOn => GetConnecteVisibility() ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectedVisibilityOff => !GetConnecteVisibility() ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StopSubscribeButtonVisibily => !GetStartVisibility() ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StartSubscribeButtonVisibily => GetStartVisibility() ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InformationWifiVisibily => DiscoveredDevices.Count != 0? Visibility.Collapsed : Visibility.Visible;
    private bool GetConnecteVisibility() => _wifiService?.CurrentStatus == WifiDirectStatus.Connected;
    private bool GetStartVisibility() => _wifiService?.WatcherStatus == WifiDirectWatcherStatus.Closed;

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
    public string CurrentStateDisplayText => _wifiService.CurrentStatus.ToString();
    public bool isnight = true;
    public SolidColorBrush CurrentStateDisplayColor
    {
        get 
        {
            
            if(!isnight)return ColorStatus(nameof(CurrentStateDisplayColor));
            return ColorStatus(nameof(CurrentStateDisplayColor),Color.Black);
        }
    }
    public SolidColorBrush ColorBlackBrush = new();
    private SolidColorBrush ColorStatus(string name,Color? color = null)
    {
        var r =color?? GetColor();
        ColorBlackBrush = new(Windows.UI.Color.FromArgb(r.A, r.R, r.G, r.B));
        if(!isnight)
        {
            _ = WaitAnimate(name);
        }
        isnight = color != null;
        return new(Windows.UI.Color.FromArgb(r.A, r.R, r.G, r.B));
    }

    private Color GetColor()
    {
        return _wifiService.CurrentStatus switch
        {

            WifiDirectStatus.Closed => Color.Orange,
            WifiDirectStatus.Connected => Color.Green,
            WifiDirectStatus.Disconnected => Color.Yellow,
            _ =>Color.Red,
        };
    }
    private async Task WaitAnimate(string name)
    {
        await Task.Delay(1500);
        OnPropertyChanged(name);
    }

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
    public bool IsFileOverDropZone { get => _isFileOverDropZone; set { _isFileOverDropZone = value; OnPropertyChanged(nameof(IsFileOverDropZone)); } }

    public ObservableCollection<WifiDirectPeer> DiscoveredDevices { get; } = [];
    public ObservableCollection<FileI> PendingFiles { get; } = [];
    public ObservableCollection<TransferHistoryEntry> RecentTransfers { get; } = [];
    

    private WifiDirectPeer? _PeerConnecte;
    public WifiDirectPeer? PeerConnecte
    {
        get => _PeerConnecte;
        private set { _PeerConnecte = value; OnPropertyChanged(nameof(PeerConnecte)); }
    }
    public string _SourcePlayer = "";
    public string SourcePlayer {
        get => _SourcePlayer;
        set
        {
            _SourcePlayer = value;
            OnPropertyChanged(nameof(SourcePlayer));
        }
    }
    public bool HasRecentTransfers => RecentTransfers.Count > 0;
    public bool HasPendingFiles => PendingFiles.Count > 0;
    public bool NoPendingFiles => !HasPendingFiles;
    public bool NoHardwardSuported => _wifiService.CurrentStatus == WifiDirectStatus.NoArdwareSupported;

    public Visibility PageReloadVisible => NoHardwardSuported ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PageReloadNoVisible => NoHardwardSuported ? Visibility.Collapsed : Visibility.Visible;

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
    public ICommand OpenSourceDirectoryCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand PlayThisCommand { get; }
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
    public event EventHandler<WifiDirectInvitationEventArgs>? InvitationRequested;

    public MainViewModel(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _wifiService = new WindowsWifiDirectService();
        ServerClientInstance = new();
        ServerClientInstance.TransfertRequest += ServerClientInstance_TransfertRequest;

        BrowseCommand = new RelayCommand(async _ => await ExecuteBrowseAsync());
        ChangeFolderCommand = new RelayCommand(async _ => await ExecuteChangeFolderAsync());
        RetryCommand = new RelayCommand(_ => RetrySearch());
        ReloadCommand = new RelayCommand(_ => ReloadPage());
        ScanCommand = new RelayCommand(_ => StartDiscovery());
        DeleteAllCommand = new RelayCommand(_ => DeleteAll());
        OpenSourceDirectoryCommand = new RelayCommand(param => OpenDirectory(param as FileI), param => param is FileI);
        OpenFileCommand = new RelayCommand(param => OpenFile(param as FileI), param => param is FileI);
        PlayThisCommand = new RelayCommand(param => PlayFile(param as FileI), param => param is FileI);
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
        _dispatcherQueue.TryEnqueue(() =>
        {
            IncomingTransferSession = arg;
            ProgressText = $"Réception de {arg.Metas.Count()} élément(s)";
        });

        while (!Accept && IncomingTransferSession != null)
        {
            await Task.Delay(3000);
        }
        bool response = Accept;
        Accept = false;
        return response;
    }

    private async Task DisconnectAsync(WifiDirectPeer? wifiDirectPeer)
    {
        _wifiService.DisconnectToPeer(ref wifiDirectPeer, default);
        PeerConnecte = wifiDirectPeer;
    }

    private void WireEvents()
    {
        ServerClientInstance.CounterClientChange += ClientInstance_CounterChanged;
        ServerClientInstance.CounterServerChange += ServerInstance_CounterChanged;
        _wifiService.StatusChanged += WifiService_StatusChanged;
        _wifiService.PublicherStatusChanged += _wifiService_PublicherStatusChanged;
        _wifiService.WatcherStatusChanged += _wifiService_WatcherStatusChanged;
        _wifiService.PeerDiscovered += WifiService_PeerDiscovered;
        _wifiService.PeerUpdated += WifiService_PeerUpdated;
        _wifiService.PeerRemoved += WifiService_PeerRemoved;
        _wifiService.InvitationReceived += WifiService_InvitationReceived;
        _wifiService.ConnectionEstablished += WifiService_ConnectionEstablished;
        _wifiService.ConnectionClosed += WifiService_ConnectionClosed;
    }

    private void _wifiService_WatcherStatusChanged(object? sender, WifiDirectWatcherStatusEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(StartSubscribeButtonVisibily));
            OnPropertyChanged(nameof(StopSubscribeButtonVisibily));
            if(_wifiService.WatcherStatus == WifiDirectWatcherStatus.Closed)
            {
                DiscoveredDevices.Clear();
                OnPropertyChanged(nameof(InformationWifiVisibily));
            }
        });
    }

    private void _wifiService_PublicherStatusChanged(object? sender, WifiDirectPublicherStatusEventArgs e) 
    { }
    private void ServerInstance_CounterChanged() => CounterServerChange();
    private void ClientInstance_CounterChanged() => CounterClientChange();

    private async Task InitializeAsync(bool reload = false)
    {
        _ctsWatcher ??= new();
        if (NoHardwardSuported)
        {
            if (reload)
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
        _ = _wifiService.StartSubscribingAsync(_ctsWatcher.Token);
    }

    private async Task ExecuteBrowseAsync()
    {
        var picker = new FileOpenPicker();
        // Nécessaire pour associer la fenêtre WinUI active au sélecteur
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);

        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var files = await picker.PickMultipleFilesAsync();
        if (files != null && files.Count > 0)
        {
            AddFiles(files.Select(f => f.Path));
        }
    }

    private async Task ExecuteChangeFolderAsync()
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);

        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            DownloadFolder = folder.Path;
        }
    }

    public void AddFiles(IEnumerable<string>? files)
    {
        if (files == null) return;
        _dispatcherQueue.TryEnqueue(() =>
        {
            foreach (var rawPath in files.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var candidate = Path.GetFullPath(rawPath.Trim());
                if (!PendingFiles.Any(item => string.Equals(item.PathFile, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    var fii = new FileI(candidate);
                    if(!PendingFiles.Contains(fii)) PendingFiles.Add(fii);
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
        catch (Exception) { }
    }

    private async Task SendAsync(WifiDirectPeer? target)
    {
        if (target == null) return;
        if (PendingFiles.Count == 0)
        {
            MessageText = "Veuillez d'abord ajouter des éléments à envoyer.";
            return;
        }

        if (_wifiService.IsConnectToPeer(target))
        {
            if (_ctsTransfert == null || !_ctsTransfert.IsCancellationRequested) _ctsTransfert = new();
            _ = ServerClientInstance.SendFilesAsync(PendingFiles, _ctsTransfert.Token);
        }
        else
        {
            MessageText = "Veuillez d'abord vous connecter.";
        }
    }

    private void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            _ctsTransfert?.Cancel();
            await Task.Delay(100);
            _ctsTransfert = new CancellationTokenSource();
            try
            {
                await ServerClientInstance.Start(e, _ctsTransfert.Token);
            }
            catch (Exception) { }
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
        AppendTransferHistory(PeerConnecte?.Name ?? "Distant", "Réception 📥", "Refusé", DownloadFolder, IncomingTransferSession.Length);
        ClearTransferRequest();
    }

    private void CancelActiveTransfert()
    {
        _ctsTransfert?.Cancel();
        ClearTransferRequest();
    }

    private void ClearTransferRequest() => IncomingTransferSession = null;
    private void DeleteAll() => PendingFiles.Clear();
    private void DeleteItem(FileI? file) { if (file != null) PendingFiles.Remove(file); }
    private void OpenDirectory(FileI? file) 
    {
        try
        {
            var p = Directory.GetParent(file?.PathFile ?? "");
            Process.Start(new ProcessStartInfo { FileName = p?.FullName,Arguments=file?.PathFile, UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }
    private void OpenFile(FileI? file)
    {
        try
        {
            if (file?.IsPlayer?? false) 
            {
                PlayFile(file);
            }
            else 
                Process.Start(new ProcessStartInfo { FileName = file?.PathFile, UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }
    CancellationTokenSource _ctsPlayer;
    private void PlayFile(FileI? file)
    {
        if(_ctsPlayer == null || _ctsPlayer.IsCancellationRequested)
            _ctsPlayer = new CancellationTokenSource();
        _=PlayFile(file?.PathFile ?? "",_ctsPlayer.Token);

    }

    private void OpenDownloadFolder() => Process.Start(new ProcessStartInfo { FileName = DownloadFolder, UseShellExecute = true });
    private void ClearTransferHistory() => RecentTransfers.Clear();
    private void ReloadPage() => _ = InitializeAsync(true);
    private void RetrySearch() => StartDiscovery();
    private void UpdateDropStatus() => DropStatusText = PendingFiles.Count == 0 ? "Aucun élément" : $"{PendingFiles.Count} prêt(s).";

    private void WifiService_StatusChanged(object? sender, WifiDirectStatusEventArgs e) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(NoHardwardSuported));
            OnPropertyChanged(nameof(PageReloadNoVisible));
            OnPropertyChanged(nameof(PageReloadVisible));
            OnPropertyChanged(nameof(CurrentStateDisplayText));
            OnPropertyChanged(nameof(ConnectedVisibilityOff));
            OnPropertyChanged(nameof(ConnectedVisibilityOn));
            OnPropertyChanged(nameof(CurrentStateDisplayColor));
        });

    private void WifiService_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e) =>
        _dispatcherQueue.TryEnqueue(() =>
        { 
            if (!DiscoveredDevices.Contains(e.Peer)) DiscoveredDevices.Add(e.Peer);
            OnPropertyChanged(nameof(InformationWifiVisibily));
        });

    private void WifiService_PeerUpdated(object? sender, WifiDirectPeerEventArgs e) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            var existing = DiscoveredDevices.FirstOrDefault(p => p.Id == e.Peer.Id);
            if (existing != null) DiscoveredDevices[DiscoveredDevices.IndexOf(existing)] = e.Peer;
            else DiscoveredDevices.Add(e.Peer);
        });

    private void WifiService_PeerRemoved(object? sender, WifiDirectPeerEventArgs e) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            var existing = DiscoveredDevices.FirstOrDefault(p => p.Id == e.Peer.Id);
            if (existing != null) DiscoveredDevices.Remove(existing);
            OnPropertyChanged(nameof(InformationWifiVisibily));
        });

    private void WifiService_InvitationReceived(object? sender, WifiDirectInvitationEventArgs e)
        => _dispatcherQueue.TryEnqueue(() => InvitationRequested?.Invoke(this, e));

    public Task RespondToInvitationAsync(WifiDirectInvitationEventArgs invitation, bool accept)
        => accept
            ? _wifiService.AcceptInvitationAsync(invitation.Token)
            : _wifiService.RejectInvitationAsync(invitation.Token);

    private void WifiService_ConnectionClosed(object? sender, WifiDirectPeerEventArgs e) => ServerClientInstance.Stop();

    private void AppendTransferHistory(string user, string type, string status, string path, long size)
    {
        _dispatcherQueue.TryEnqueue(() =>
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
    private Window? ChildWindow = null;
    private MediaPlayerElement? Player = null;
    private bool isClose = true;
    public async Task PlayFile(string path,CancellationToken token)
    {
        bool active = ChildWindow == null;
        if (string.IsNullOrEmpty(path)) return;
        var mediaSource = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path));

        Player = new MediaPlayerElement()
        {
            Source = mediaSource,
            AutoPlay = true,
            AreTransportControlsEnabled = true
        };
        // Ensure you close the child window before closing the parent window to avoid application crash.
        ChildWindow = isClose? new Window()
        {
            ExtendsContentIntoTitleBar = true,
            SystemBackdrop = new MicaBackdrop(),
            Content = new Page()
            {
                Content = Player
            }
        } : ChildWindow;
        if (isClose)
        {
            isClose = false;
            ChildWindow?.AppWindow.ResizeClient(new SizeInt32(400,100));
            ChildWindow?.Activate();
            ChildWindow?.Closed += (_, _) => { isClose = true; Player = null; };
            token.Register(() => { ChildWindow?.Close(); ChildWindow = null; Player = null; });
        }
    }
}