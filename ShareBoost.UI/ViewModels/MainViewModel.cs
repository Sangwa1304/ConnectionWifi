using LibraryMI.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ServiceTcpChared;
using System;
using System.Drawing;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using WindowsWifiLibrary.Models;
using WindowsWifiLibrary.Services;
using Microsoft.UI.Xaml.Media.Imaging;

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
        private Task? _serverTask;
        private Task? _clientTask;
        private CancellationTokenSource? _sessionCancellation;
        private volatile bool _isRunning;
        internal bool IsRunning => _isRunning;

        internal void Stop()
        {
            _isRunning = false;
            _sessionCancellation?.Cancel();
            ServerInstance?.Stop();
            ClientInstance?.Stop();
        }

        private Task<bool> ServerClient_FilesComming(IEnumerable<InfoMeta> arg)
        {
            return TransfertRequest?.Invoke(new(arg)) ?? Task.FromResult(false);
        }

        private void ServerClient_CounterChange() => CounterServerChange?.Invoke();
        private void Client_CounterChange() => CounterClientChange?.Invoke();
        private void ServerClient_FileChange() => FileClientChange?.Invoke();
        private void ServerInstance_FileChange() => FileServerChange?.Invoke();

        internal Task<bool> SendFilesAsync(IEnumerable<FileI> pendingFiles, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            List<string> f = [.. from e in pendingFiles select e.PathFile];
            if (ServerInstance is null || !IsRunning)
            {
                throw new InvalidOperationException("Les canaux TCP ne sont pas prêts. La reconnexion est en cours.");
            }

            return ServerInstance.EnqueueAsync(f, token);
        }

        internal async Task StartAsync(WifiDirectConnectionEventArgs e, CancellationToken token)
        {
            Stop();
            _sessionCancellation?.Dispose();
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken sessionToken = _sessionCancellation.Token;

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
                _serverTask = ServerInstance.StartConnection(sessionToken);
                _clientTask = ClientInstance.StartConnection(sessionToken);
            }
            else
            {
                _clientTask = ClientInstance.StartConnection(sessionToken);
                _serverTask = ServerInstance.StartConnection(sessionToken);
            }

            _isRunning = true;
            Task serverTask = _serverTask;
            Task clientTask = _clientTask;
            Task firstCompleted = await Task.WhenAny(serverTask, clientTask).ConfigureAwait(false);
            Exception? failure = null;

            try
            {
                await firstCompleted.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                _sessionCancellation.Cancel();
                ServerInstance?.Stop();
                ClientInstance?.Stop();
            }

            try
            {
                await Task.WhenAll(serverTask, clientTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            _isRunning = false;

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
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
    public Visibility InformationWifiVisibily => DiscoveredDevices.Count != 0 && !NoHardwardSuported? Visibility.Collapsed : Visibility.Visible;
    public Visibility InformationWifiOffVisibily => PageReloadVisible;
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
    public bool isnight = false;
    public SolidColorBrush CurrentStateDisplayColor
    {
        get 
        {
            if(isnight)
            {
                return ColorStatus(nameof(CurrentStateDisplayColor), Color.Black); 
            }
            return ColorStatus(nameof(CurrentStateDisplayColor));
        }
    }
    private SolidColorBrush ColorStatus(string name,Color? color = null)
    {
        var r =color?? GetColor();
        _ = WaitAnimate(name);
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
        await Task.Delay(1000);
        isnight = !isnight;
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
    private CancellationTokenSource? _ctsNetworkSession;
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly SemaphoreSlim _networkStartGate = new(1, 1);
    private TaskCompletionSource<bool>? _incomingTransferDecision;
    private int _invitationPromptActive;
    private bool _isTransferActive;
    private bool _disposed;
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
    Func<DispatcherQueue> Dispatcher;

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
        ConnectAndSendCommand = new RelayCommand(async param => await ConnectAndSendAsync(param as WifiDirectPeer), CanStartPeerOperation);
        ConnectCommand = new RelayCommand(async param => await ConnectAsync(param as WifiDirectPeer), CanStartPeerOperation);
        SendCommand = new RelayCommand(async param => await SendAsync(param as WifiDirectPeer), CanStartPeerOperation);
        DisconnectCommand = new RelayCommand(async param => await DisconnectAsync(param as WifiDirectPeer), param => param is WifiDirectPeer);
        AcceptTransferCommand = new RelayCommand(async _ => await AcceptIncomingTransferAsync(), _ => IncomingTransferSession != null);
        RejectTransferCommand = new RelayCommand(_ => RejectIncomingTransfer(), _ => IncomingTransferSession != null);
        CancelTransferCommand = new RelayCommand(_ => CancelActiveTransfert(), _ => _isTransferActive || IncomingTransferSession != null);
        OpenDownloadFolderCommand = new RelayCommand(_ => OpenDownloadFolder(), _ => !string.IsNullOrWhiteSpace(DownloadFolder));
        ClearTransferHistoryCommand = new RelayCommand(_ => ClearTransferHistory(), _ => RecentTransfers.Count > 0);

        PendingFiles.CollectionChanged += (_, __) => { OnPropertyChanged(nameof(HasPendingFiles)); OnPropertyChanged(nameof(NoPendingFiles)); UpdateDropStatus(); };
        RecentTransfers.CollectionChanged += (_, __) => { OnPropertyChanged(nameof(HasRecentTransfers)); ((RelayCommand)ClearTransferHistoryCommand).RaiseCanExecuteChanged(); };

        WireEvents();
        _ = InitializeAsync();
    }

    private async Task<bool> ServerClientInstance_TransfertRequest(InfoMetas arg)
    {
        if (_disposed || arg.Metas is null || !arg.Metas.Any())
        {
            return false;
        }

        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _incomingTransferDecision, decision, null) is not null)
        {
            return false;
        }

        Accept = false;
        _dispatcherQueue.TryEnqueue(() =>
        {
            IncomingTransferSession = arg;
            ProgressText = $"Réception de {arg.Metas.Count()} élément(s)";
            RaiseTransferCommandStates();
        });

        try
        {
            return await decision.Task.WaitAsync(
                TimeSpan.FromMinutes(5),
                _ctsNetworkSession?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            _dispatcherQueue.TryEnqueue(() => MessageText = "La demande de réception a expiré.");
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _incomingTransferDecision, null, decision);
            _dispatcherQueue.TryEnqueue(() =>
            {
                Accept = false;
                ClearTransferRequest();
                RaiseTransferCommandStates();
            });
        }
    }

    private bool CanStartPeerOperation(object? parameter) => parameter is WifiDirectPeer && !_isTransferActive && !_disposed;

    private void RaiseTransferCommandStates()
    {
        ((RelayCommand)ConnectAndSendCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SendCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelTransferCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AcceptTransferCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RejectTransferCommand).RaiseCanExecuteChanged();
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
        _wifiService.Error += WifiService_Error;
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
    private void ServerInstance_CounterChanged() => _dispatcherQueue.TryEnqueue(CounterServerChange);
    private void ClientInstance_CounterChanged() => _dispatcherQueue.TryEnqueue(CounterClientChange);
    
    private async Task InitializeAsync(bool reload = false)
    {
        if (reload || _ctsWatcher is null || _ctsWatcher.IsCancellationRequested)
        {
            _ctsWatcher?.Cancel();
            _ctsWatcher?.Dispose();
            _ctsWatcher = new CancellationTokenSource();
        }

        await _wifiService.InitializeAsync(_ctsWatcher.Token);
        if (NoHardwardSuported)
        {
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

        foreach (var rawPath in files.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var candidate = Path.GetFullPath(rawPath.Trim());
            if (!PendingFiles.Any(item => string.Equals(item.PathFile, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                var fii = new FileI(candidate);
                if (!PendingFiles.Contains(fii))
                {
                    PendingFiles.Add(fii);
                    FileMiIcon.Add(fii, Pup,()=>_dispatcherQueue);

                }
            }
        }
    }

    private async void Pup(FileI fii)
    {
        
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (PendingFiles.Contains(fii)) PendingFiles.Remove(fii);
            PendingFiles.Add(fii);

        });
    }

    private async Task ConnectAndSendAsync(WifiDirectPeer? target)
    {
        if (!await ConnectAsync(target))
        {
            return;
        }

        await SendAsync(target);
    }

    private async Task<bool> ConnectAsync(WifiDirectPeer? target)
    {
        if (target == null || _disposed || _isTransferActive) return false;
        try
        {
            _ctsConnection?.Cancel();
            _ctsConnection?.Dispose();
            SelectedPeerName = target.Name;
            PeerConnecte = target;
            _ctsConnection = new CancellationTokenSource();
            bool connected = await _wifiService.ConnectToPeerAsync(target, _ctsConnection.Token);
            if (!connected)
            {
                PeerConnecte = null;
                SelectedPeerName = "Aucun appareil sélectionné";
                MessageText = "La connexion à l’appareil a échoué.";
            }

            return connected;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            MessageText = $"Connexion impossible : {ex.Message}";
            return false;
        }
    }

    private async Task SendAsync(WifiDirectPeer? target)
    {
        if (target == null || _disposed) return;
        if (PendingFiles.Count == 0)
        {
            MessageText = "Veuillez d'abord ajouter des éléments à envoyer.";
            return;
        }

        if (!_wifiService.IsConnectToPeer(target))
        {
            MessageText = "Veuillez d'abord vous connecter.";
            return;
        }

        if (!ServerClientInstance.IsRunning)
        {
            MessageText = "Les canaux TCP ne sont pas prêts. Déconnectez puis reconnectez l’appareil pour rétablir le transfert.";
            return;
        }

        if (!await _transferGate.WaitAsync(0))
        {
            MessageText = "Un transfert est déjà en cours.";
            return;
        }

        _isTransferActive = true;
        RaiseTransferCommandStates();
        try
        {
            _ctsTransfert?.Cancel();
            _ctsTransfert?.Dispose();
            _ctsTransfert = new CancellationTokenSource();
            bool accepted = await ServerClientInstance.SendFilesAsync(PendingFiles.ToArray(), _ctsTransfert.Token);
            MessageText = accepted ? "Transfert terminé." : "Le transfert a été refusé par le destinataire.";
        }
        catch (OperationCanceledException)
        {
            MessageText = "Transfert annulé.";
        }
        catch (Exception ex)
        {
            MessageText = $"Impossible de démarrer le transfert : {ex.Message}";
        }
        finally
        {
            _isTransferActive = false;
            _transferGate.Release();
            RaiseTransferCommandStates();
        }
    }

    private void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e)
    {
        if (!_dispatcherQueue.TryEnqueue(() => _ = StartNetworkSessionAsync(e)))
        {
            ServerClientInstance.Stop();
            _wifiService.Stop();
        }
    }

    private async Task StartNetworkSessionAsync(WifiDirectConnectionEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        await _networkStartGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _ctsNetworkSession?.Cancel();
            _ctsNetworkSession?.Dispose();
            _ctsNetworkSession = new CancellationTokenSource();
            CancellationToken sessionToken = _ctsNetworkSession.Token;
            Exception? lastFailure = null;

            for (int attempt = 1; attempt <= 4 && !sessionToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    MessageText = attempt == 1
                        ? "Connexion des canaux de transfert…"
                        : $"Rétablissement des canaux TCP ({attempt}/4)…";
                    await ServerClientInstance.StartAsync(e, sessionToken);
                    if (!sessionToken.IsCancellationRequested)
                    {
                        throw new IOException("La session TCP s’est fermée sans annulation demandée.");
                    }

                    return;
                }
                catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    if (attempt < 4 && !sessionToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), sessionToken);
                    }
                }
            }

            if (!_disposed && !sessionToken.IsCancellationRequested && lastFailure is not null)
            {
                MessageText = $"Les canaux TCP n’ont pas pu être rétablis après 4 tentatives : {lastFailure.Message} Déconnectez puis reconnectez l’appareil pour réessayer.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _networkStartGate.Release();
        }
    }

    private async Task AcceptIncomingTransferAsync()
    {
        if (IncomingTransferSession == null) return;
        Accept = true;
        AppendTransferHistory(PeerConnecte?.Name ?? "Distant", "Réception 📥", "debuté", DownloadFolder, IncomingTransferSession.Length);
        _incomingTransferDecision?.TrySetResult(true);
        RaiseTransferCommandStates();
    }

    private void RejectIncomingTransfer()
    {
        if (IncomingTransferSession == null) return;
        AppendTransferHistory(PeerConnecte?.Name ?? "Distant", "Réception 📥", "Refusé", DownloadFolder, IncomingTransferSession.Length);
        Accept = false;
        _incomingTransferDecision?.TrySetResult(false);
        ClearTransferRequest();
        RaiseTransferCommandStates();
    }

    private void CancelActiveTransfert()
    {
        _ctsTransfert?.Cancel();
        _incomingTransferDecision?.TrySetResult(false);
        ClearTransferRequest();
        RaiseTransferCommandStates();
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
            if (file?.IsPlayer ?? false)
            {
                if(file?.IsMusic?? false)
                    PlayFile(file);
                else PlayFile(file,false);
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = file?.PathFile, UseShellExecute = true });
            }
        }
        catch (Exception)
        {
        }
    }
    CancellationTokenSource _ctsPlayer;
    private void PlayFile(FileI? file,bool Control = true)
    {
        if(_ctsPlayer == null || _ctsPlayer.IsCancellationRequested)
            _ctsPlayer = new CancellationTokenSource();
        _=PlayFile(file?.PathFile ?? "",_ctsPlayer.Token, Control);

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
    {
        if (_disposed || Interlocked.CompareExchange(ref _invitationPromptActive, 1, 0) != 0)
        {
            _ = _wifiService.RejectInvitationAsync(e.Token);
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            if (InvitationRequested is null)
            {
                _ = RespondToInvitationAsync(e, false);
                return;
            }

            try
            {
                InvitationRequested.Invoke(this, e);
            }
            catch (Exception ex)
            {
                MessageText = $"Impossible d’afficher la demande de connexion : {ex.Message}";
                _ = RespondToInvitationAsync(e, false);
            }
        }))
        {
            Interlocked.Exchange(ref _invitationPromptActive, 0);
            _ = _wifiService.RejectInvitationAsync(e.Token);
        }
    }

    public async Task RespondToInvitationAsync(WifiDirectInvitationEventArgs invitation, bool accept)
    {
        try
        {
            if (accept)
            {
                bool connected = await _wifiService.AcceptInvitationAsync(invitation.Token);
                if (!connected)
                {
                    MessageText = "L’invitation a expiré ou la connexion Wi‑Fi Direct a échoué.";
                }
            }
            else
            {
                await _wifiService.RejectInvitationAsync(invitation.Token);
            }
        }
        catch (Exception ex)
        {
            MessageText = $"Impossible de traiter l’invitation : {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _invitationPromptActive, 0);
        }
    }

    private void WifiService_ConnectionClosed(object? sender, WifiDirectPeerEventArgs e)
    {
        _ctsNetworkSession?.Cancel();
        ServerClientInstance.Stop();
        _dispatcherQueue.TryEnqueue(() =>
        {
            PeerConnecte = null;
            MessageText = "La connexion Wi‑Fi Direct a été fermée.";
            RaiseTransferCommandStates();
        });
    }

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

    private void WifiService_Error(object? sender, WifiDirectErrorEventArgs e)
    {
        string message = string.IsNullOrWhiteSpace(e.message) ? "Une erreur Wi‑Fi Direct est survenue." : e.message;
        _dispatcherQueue.TryEnqueue(() => MessageText = message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _incomingTransferDecision?.TrySetResult(false);
        _ctsWatcher?.Cancel();
        _ctsTransfert?.Cancel();
        _ctsNetworkSession?.Cancel();
        _ctsConnection?.Cancel();
        _ctsPlayer?.Cancel();
        ServerClientInstance.Stop();
        _wifiService.Dispose();
        _ctsWatcher?.Dispose();
        _ctsTransfert?.Dispose();
        _ctsNetworkSession?.Dispose();
        _ctsConnection?.Dispose();
        _wifiService.Error -= WifiService_Error;
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
    public MediaSource _PlayerSource = default;
    public MediaSource PlayerSource
    {
        get => _PlayerSource;

        set
        {
            _PlayerSource = value;

            OnPropertyChanged(nameof(PlayerSource));
        }
    }
    public bool _OpenTeacher = false;
    public bool OpenTeacher
    {
        get => _OpenTeacher;

        set
        {
            _OpenTeacher = value;

            OnPropertyChanged(nameof(OpenTeacher));
        }
    }
    public event Action ShowTeach;
    public void PopIsClose()
    {
        PlayerSource?.Dispose();
    }
    public async Task PlayFile(string path, CancellationToken token, bool control = true)
    {

        PlayerSource?.Dispose();
        if (string.IsNullOrEmpty(path)) return;
        PlayerSource = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path));
        
        if(ChildWindow != null) 
        {
            if (control) ChildWindow?.AppWindow.ResizeClient(new SizeInt32(400, 100));
            else ChildWindow?.AppWindow.ResizeClient(new SizeInt32(500, 450));
            ((ChildWindow.Content as Page)?.Content as MediaPlayerElement)?.AreTransportControlsEnabled = control;
            ((ChildWindow.Content as Page)?.Content as MediaPlayerElement)?.Source = PlayerSource;
            return;
        }

        ChildWindow = new Window()
        {
            ExtendsContentIntoTitleBar = true,
            SystemBackdrop = new MicaBackdrop(),
            Content = new Page()
            {
                Content = new MediaPlayerElement()
                {
                    Source = PlayerSource,
                    AutoPlay = true,
                    AreTransportControlsEnabled = control
                }
            }
        };
        if (control)ChildWindow?.AppWindow.ResizeClient(new SizeInt32(400,100));
        else ChildWindow?.AppWindow.ResizeClient(new SizeInt32(500, 450));
        ChildWindow?.Activate();
        ChildWindow?.Closed += (_, _) => { ChildWindow = null; PlayerSource?.Dispose(); };
        token.Register(() => PlayerSource?.Dispose());
        token.Register(() => { ChildWindow?.Close(); ChildWindow = null; });
    }
}
