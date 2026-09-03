using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using WindowsWifiLibrary.Core.Models;
using WindowsWifiLibrary.Core.Services;
using WindowsWifiLibrary.Models;
using WindowsWifiLibrary.Services;
using WindowsWifiWpf.Models;

namespace WindowsWifiWpf.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged
{
    private readonly WindowsWifiDirectService _wifiService;
    private readonly FileTransferService _transferService;

    private TransferWorkflowState _currentState = TransferWorkflowState.Idle;
    public TransferWorkflowState CurrentState
    {
        get => _currentState;
        set
        {
            _currentState = value;
            OnPropertyChanged(nameof(CurrentState));
            OnPropertyChanged(nameof(CurrentStateDisplayText));
        }
    }

    public string CurrentStateDisplayText => CurrentState switch
    {
        TransferWorkflowState.DiscoveringPeers => "Recherche des appareils...",
        TransferWorkflowState.PeerSelected => "Appareil sélectionné",
        TransferWorkflowState.PairingRequested => "Appairage en cours...",
        TransferWorkflowState.TransferNegotiation => "Négociation du transfert...",
        TransferWorkflowState.Sending => "Envoi en cours...",
        TransferWorkflowState.Receiving => "Réception en cours...",
        TransferWorkflowState.Completed => "Terminé",
        TransferWorkflowState.Cancelled => "Annulé",
        TransferWorkflowState.Error => "Erreur",
        _ => "Prêt"
    };

    private string _selectedPeerName = "Aucun appareil sélectionné";
    private string _dropStatusText = "Aucun élément sélectionné";
    private string _messageText = string.Empty;
    private string _progressText = "En attente...";
    private string _downloadFolder = GetDefaultDownloadFolder();
    private double _transferProgress;
    private TransferSessionMetadata? _incomingTransferSession;
    private bool _isFileOverDropZone;

    public string SelectedPeerName { get => _selectedPeerName; set { _selectedPeerName = value; OnPropertyChanged(nameof(SelectedPeerName)); } }
    public string DropStatusText { get => _dropStatusText; set { _dropStatusText = value; OnPropertyChanged(nameof(DropStatusText)); } }
    public string MessageText { get => _messageText; set { _messageText = value; OnPropertyChanged(nameof(MessageText)); } }
    public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(nameof(ProgressText)); } }
    
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
    
    public WifiDirectPeer? PeerWaiting { get; private set; }
    public bool HasRecentTransfers => RecentTransfers.Count > 0;
    public bool HasPendingFiles => PendingFiles.Count > 0;
    public bool NoPendingFiles => !HasPendingFiles;

    public TransferSessionMetadata? IncomingTransferSession
    {
        get => _incomingTransferSession;
        set
        {
            _incomingTransferSession = value;
            OnPropertyChanged(nameof(IncomingTransferSession));
            OnPropertyChanged(nameof(IsTransferRequestVisible));
            OnPropertyChanged(nameof(TransferRequestSummary));
            OnPropertyChanged(nameof(CurrentTransferStatusText));
        }
    }

    public bool IsTransferRequestVisible => IncomingTransferSession != null;
    public string TransferRequestSummary => IncomingTransferSession == null
        ? string.Empty
        : $"{IncomingTransferSession.SenderName} souhaite envoyer {IncomingTransferSession.TotalFiles} élément(s) ({FormatSize(IncomingTransferSession.TotalSize)}).";

    public string CurrentTransferStatusText => CurrentState switch
    {
        TransferWorkflowState.Sending => "Envoi en cours",
        TransferWorkflowState.Receiving => "Réception en cours",
        TransferWorkflowState.TransferNegotiation => "Négociation du transfert",
        TransferWorkflowState.Completed => "Transfert terminé",
        TransferWorkflowState.Cancelled => "Transfert annulé",
        TransferWorkflowState.Error => "Erreur de transfert",
        _ => "Aucun transfert actif"
    };

    private CancellationTokenSource? _ctsWatcher;
    private CancellationTokenSource? _ctsTransfert;
    private CancellationTokenSource? _ctsConction;

    public ICommand BrowseCommand { get; }
    public ICommand ChangeFolderCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public ICommand DeleteItemCommand { get; }
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
        _transferService = new FileTransferService();

        // Synchronisation inter-threads sécurisée pour les collections de l'interface graphique
        BindingOperations.EnableCollectionSynchronization(DiscoveredDevices, new object());
        BindingOperations.EnableCollectionSynchronization(PendingFiles, new object());
        BindingOperations.EnableCollectionSynchronization(RecentTransfers, new object());

        WireEvents();
        _ = InitializeAsync();

        BrowseCommand = new RelayCommand(async _ => await ExecuteBrowseAsync());
        ChangeFolderCommand = new RelayCommand(_ => ExecuteChangeFolder());
        RetryCommand = new RelayCommand(_ => RetryPage());
        ReloadCommand = new RelayCommand(_ => Reload());
        ScanCommand = new RelayCommand(_ => StartDiscovery());
        DeleteAllCommand = new RelayCommand(_ => DeleteAll());
        DeleteItemCommand = new RelayCommand(param => DeleteItem(param as FileI), param => param is FileI);
        ConnectAndSendCommand = new RelayCommand(async param => await ConnectAndSendAsync(param as WifiDirectPeer), param => param is WifiDirectPeer);
        AcceptTransferCommand = new RelayCommand(async _ => await AcceptIncomingTransferAsync(), _ => IncomingTransferSession != null);
        RejectTransferCommand = new RelayCommand(_ => RejectIncomingTransfer(), _ => IncomingTransferSession != null);
        CancelTransferCommand = new RelayCommand(_ => CancelActiveTransfer(), _ => CurrentState == TransferWorkflowState.Sending || CurrentState == TransferWorkflowState.Receiving || CurrentState == TransferWorkflowState.TransferNegotiation);
        OpenDownloadFolderCommand = new RelayCommand(_ => OpenDownloadFolder(), _ => !string.IsNullOrWhiteSpace(DownloadFolder));
        ClearTransferHistoryCommand = new RelayCommand(_ => ClearTransferHistory(), _ => RecentTransfers.Count > 0);

        PendingFiles.CollectionChanged += (_, __) => { OnPropertyChanged(nameof(HasPendingFiles)); OnPropertyChanged(nameof(NoPendingFiles)); UpdateDropStatus(); };
        RecentTransfers.CollectionChanged += (_, __) => { OnPropertyChanged(nameof(HasRecentTransfers)); ((RelayCommand)ClearTransferHistoryCommand).RaiseCanExecuteChanged(); };

        SetState(TransferWorkflowState.Idle, "Prêt à démarrer un transfert.");
    }

    private void WireEvents()
    {
        _wifiService.StatusChanged += WifiService_StatusChanged;
        _wifiService.PeerDiscovered += WifiService_PeerDiscovered;
        _wifiService.PeerUpdated += WifiService_PeerUpdated;
        _wifiService.PeerRemoved += WifiService_PeerRemoved;
        _wifiService.InvitationReceived += WifiService_InvitationReceived;
        _wifiService.ConnectionEstablished += WifiService_ConnectionEstablished;
        _wifiService.ConnectionClosed += WifiService_ConnectionClosed;
        _transferService.IncomingTransferRequested += OnIncomingTransferRequested;
    }

    private async Task InitializeAsync()
    {
        try
        {
            bool initialized = await _wifiService.InitializeAsync();
            if (!initialized || !_wifiService.IsHardwareSupported)
            {
                _wifiService.Stop();
                SetState(TransferWorkflowState.Error, "Matériel Wi-Fi Direct non supporté.");
                return;
            }
            _ = _wifiService.StartPublishingAsync();
            SetState(TransferWorkflowState.Idle, "Service Wi-Fi Direct prêt.");
        }
        catch (Exception ex)
        {
            SetState(TransferWorkflowState.Error, "Initialisation impossible : " + ex.Message);
        }
    }

    private void StartDiscovery()
    {
        SetState(TransferWorkflowState.DiscoveringPeers, "Recherche des appareils disponibles...");
        _ctsWatcher?.Cancel();
        DiscoveredDevices.Clear();
        _ctsWatcher = new CancellationTokenSource();
        _wifiService.StartSubscribingAsync(_ctsWatcher.Token);
    }

    private void SetState(TransferWorkflowState state, string? statusMessage = null)
    {
        CurrentState = state;
        if (!string.IsNullOrWhiteSpace(statusMessage)) MessageText = statusMessage;
        Application.Current.Dispatcher.Invoke(() =>
        {
            ((RelayCommand)CancelTransferCommand).RaiseCanExecuteChanged();
        });
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
            if (added > 0) SetState(TransferWorkflowState.PeerSelected, $"{PendingFiles.Count} élément(s) prêt(s). Sélectionnez un appareil.");
        });
    }

    private async Task ConnectAndSendAsync(WifiDirectPeer? target)
    {
        if (target == null) return;
        if (PendingFiles.Count == 0)
        {
            MessageBox.Show("Veuillez d'abord glisser-déposer des éléments à envoyer.", "Liste vide", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            SelectedPeerName = target.Name;
            PeerWaiting = target;
            SetState(TransferWorkflowState.PairingRequested, $"Connexion et envoi vers {target.Name}...");
            _ctsConction = new CancellationTokenSource();
            await _wifiService.ConnectToPeerAsync(target, _ctsConction.Token);
        }
        catch (Exception ex)
        {
            SetState(TransferWorkflowState.Error, "Erreur de liaison : " + ex.Message);
        }
    }

    private void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            SetState(TransferWorkflowState.TransferNegotiation, "⚡ Connexion sécurisée établie. Initialisation du canal...");
            
            var localIp = e.LocalIpAddress;
            const int port = 5050;

            _ctsTransfert = new CancellationTokenSource();
            _ = Task.Run(() => _transferService.StartListeningAsync(localIp, port, _ctsTransfert.Token));

            await Task.Delay(300);

            if (PendingFiles.Count > 0 && PeerWaiting != null)
            {
                SetState(TransferWorkflowState.Sending, "Envoi du catalogue de métadonnées...");
                var metadata = await _transferService.PrepareMetadataAsync(FileI.AsEnumerable(PendingFiles), localIp, port);
                
                PeerDevice device = new() 
                {
                    Name = PeerWaiting.Name, 
                    Id = PeerWaiting.Id,
                    Port = port,
                    IpAddress = e.RemoteIpAddress
                };
                
                bool success = await _transferService.RequestPairingAsync(device, metadata, _ctsTransfert.Token);
                
                if (success)
                {
                    AppendTransferHistory(PeerWaiting.Name, "Envoi 🚀", "Réussi", "P2P Link", metadata.TotalSize);
                    ResetWorkflowAfterTimeout("🚀 Tous les fichiers ont été transmis avec succès !");
                }
                else
                {
                    AppendTransferHistory(PeerWaiting.Name, "Envoi 🚀", "Refusé", "P2P Link", metadata.TotalSize);
                    SetState(TransferWorkflowState.Cancelled, "L'appareil distant a refusé la transaction.");
                    ResetWorkflowAfterTimeout("Transfert interrompu.");
                }
            }
        });
    }

    private async Task AcceptIncomingTransferAsync()
    {
        if (IncomingTransferSession == null) return;
        var session = IncomingTransferSession;
        try
        {
            _ctsTransfert = new CancellationTokenSource();
            var decision = await _transferService.AcceptTransferAsync(session, DownloadFolder, _ctsTransfert.Token);
            
            var progress = new Progress<FileTransferProgress>(p =>
            {
                TransferProgress = p.Percentage;
                ProgressText = $"{p.CurrentFileName} ({p.Percentage:0.0}%)";
                SetState(TransferWorkflowState.Receiving);
            });

            await _transferService.DownloadSessionAsync(session, decision.DestinationFolder!, progress, _ctsTransfert.Token);
            AppendTransferHistory(session.SenderName ?? "Distant", "Réception 📥", "Terminé", decision.DestinationFolder!, session.TotalSize);
            ResetWorkflowAfterTimeout("Réception complétée !");
        }
        catch (Exception ex)
        {
            AppendTransferHistory(session.SenderName ?? "Distant", "Réception 📥", "Échec", DownloadFolder, session.TotalSize);
            SetState(TransferWorkflowState.Error, "Erreur : " + ex.Message);
        }
    }

    private void RejectIncomingTransfer()
    {
        if (IncomingTransferSession == null) return;
        _ = _transferService.CancelSessionAsync(IncomingTransferSession.SessionId, CancellationToken.None);
        AppendTransferHistory(IncomingTransferSession.SenderName ?? "Distant", "Réception 📥", "Refusé", DownloadFolder, IncomingTransferSession.TotalSize);
        SetState(TransferWorkflowState.Cancelled, "Transfert refusé.");
        ClearTransferRequest();
    }

    private void CancelActiveTransfer()
    {
        _ctsTransfert?.Cancel();
        SetState(TransferWorkflowState.Cancelled, "Annulé par l'utilisateur.");
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
                SetState(TransferWorkflowState.Idle, endMessage);
            });
        });
    }

    private void ClearTransferRequest() => IncomingTransferSession = null;
    private void DeleteAll() { PendingFiles.Clear(); SetState(TransferWorkflowState.Idle, "Liste vidée."); }
    private void DeleteItem(FileI? file) { if (file != null) PendingFiles.Remove(file); }
    private void OpenDownloadFolder() => Process.Start(new ProcessStartInfo { FileName = DownloadFolder, UseShellExecute = true });
    private void ClearTransferHistory() => RecentTransfers.Clear();
    private void Reload() => StartDiscovery();
    private void RetryPage() { _wifiService.Stop(); _ = InitializeAsync(); }
    private void UpdateDropStatus() => DropStatusText = PendingFiles.Count == 0 ? "Aucun élément" : $"{PendingFiles.Count} prêt(s).";
    
    private void WifiService_StatusChanged(object? sender, WifiDirectStatusEventArgs e) => Application.Current.Dispatcher.Invoke(() => MessageText = $"Statut matériel : {e.Status}");
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

    private void WifiService_ConnectionClosed(object? sender, WifiDirectConnectionEventArgs e) { }
    
    private void OnIncomingTransferRequested(object? sender, TransferSessionMetadata m) 
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IncomingTransferSession = m;
            SetState(TransferWorkflowState.TransferNegotiation, $"Demande de {m.SenderName}.");
            ProgressText = $"Réception de {m.TotalFiles} élément(s)";
        });
    }

    private void AppendTransferHistory(string user, string type, string status, string path, long size)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RecentTransfers.Insert(0, new TransferHistoryEntry { Name = user, Type = type, Status = status, Path = path, Size = size, Timestamp = DateTime.Now });
        });
    }

    private static string GetDefaultDownloadFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Wi-Fi P2P");

    private static string FormatSize(long size)
    {
        if (size < 1024) return $"{size} o";
        if (size < 1024 * 1024) return $"{size / 1024.0:0.##} Ko";
        if (size < 1024 * 1024 * 1024) return $"{size / (1024.0 * 1024):0.##} Mo";
        return $"{size / (1024.0 * 1024 * 1024):0.##} Go";
    }

    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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