using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using WindowsWifiLibrary.Core.Models;
using WindowsWifiLibrary.Core.Services;
using WindowsWifiLibrary.Models;
using WindowsWifiLibrary.Services;
using WindowsWifiWpf.Models;

namespace WindowsWifiWpf.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged
{
    // Services
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
        TransferWorkflowState.DiscoveringPeers => "Recherche des appareils",
        TransferWorkflowState.PeerSelected => "Appareil sélectionné",
        TransferWorkflowState.PairingRequested => "Appairage en cours",
        TransferWorkflowState.TransferNegotiation => "Négociation du transfert",
        TransferWorkflowState.Sending => "Envoi en cours",
        TransferWorkflowState.Receiving => "Réception en cours",
        TransferWorkflowState.Completed => "Terminé",
        TransferWorkflowState.Cancelled => "Annulé",
        TransferWorkflowState.Error => "Erreur",
        _ => "Prêt"
    };

    private string _selectedPeerName = "Aucun appareil sélectionné";
    private string _lanceScan = "lancer la recherche";

    public string LanceScan
    {
        get => _lanceScan;

        set { _lanceScan = value; OnPropertyChanged(nameof(LanceScan)); }
    }
    public string SelectedPeerName
    {
        get => _selectedPeerName;
        set { _selectedPeerName = value; OnPropertyChanged(nameof(SelectedPeerName)); }
    }

    // State
    public ObservableCollection<WifiDirectPeer> DiscoveredDevices { get; } = [];
    public ObservableCollection<FileI> PendingFiles { get; } = [];
    public ObservableCollection<TransferHistoryEntry> RecentTransfers { get; } = [];
    public WifiDirectPeer? PeerWaiting { get; private set; }
    
    private string _dropStatusText = "Aucun élément sélectionné";
    public string DropStatusText { get => _dropStatusText; set { _dropStatusText = value; OnPropertyChanged(nameof(DropStatusText)); } }

    private string _messageText = string.Empty;
    public string MessageText { get => _messageText; set { _messageText = value; OnPropertyChanged(nameof(MessageText)); } }

    private string _progressText = "En attente...";
    public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(nameof(ProgressText)); } }

    private string _downloadFolder = GetDefaultDownloadFolder();
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

    private double _transferProgress;
    public double TransferProgress { get => _transferProgress; set { _transferProgress = value; OnPropertyChanged(nameof(TransferProgress)); } }

    public bool HasRecentTransfers => RecentTransfers.Count > 0;

    private TransferSessionMetadata? _incomingTransferSession;
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

    public string CurrentTransferStatusText =>
        CurrentState switch
        {
            TransferWorkflowState.Sending => "Envoi en cours",
            TransferWorkflowState.Receiving => "Réception en cours",
            TransferWorkflowState.TransferNegotiation => "Négociation du transfert",
            TransferWorkflowState.Completed => "Transfert terminé",
            TransferWorkflowState.Cancelled => "Transfert annulé",
            TransferWorkflowState.Error => "Erreur de transfert",
            _ => "Aucun transfert actif"
        };

    public bool HasPendingFiles => PendingFiles.Count > 0;
    public bool NoPendingFiles => !HasPendingFiles;

    private CancellationTokenSource? _ctsWatcher;
    private CancellationTokenSource? _ctsTransfert;
    private CancellationTokenSource? _ctsInvitation;
    private CancellationTokenSource? _ctsConction;

    // Commands
    public ICommand BrowseCommand { get; }
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

    // Initialise le ViewModel, instancie les services et configure les commandes et les observateurs.
    public MainViewModel()
    {
        _wifiService = new();
        _transferService = new();

        WireEvents();
        _ = InitializeAsync();

        // Commands
        BrowseCommand = new RelayCommand(async _ => await ExecuteBrowseAsync());
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

    // Souscrit les événements du service Wi‑Fi Direct et du service de transfert pour refléter l'état en temps réel.
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

    // Initialise le support Wi‑Fi Direct et met le service en écoute pour les événements de découverte et de connexion.
    private async Task InitializeAsync()
    {
        try
        {
            bool initialized = await _wifiService.InitializeAsync();
            if (!initialized || !_wifiService.IsHardwareSupported)
            {
                _wifiService.Stop();
                MessageText = "Hardware Wi-Fi Direct non supporté";
                SetState(TransferWorkflowState.Error, "Hardware Wi‑Fi Direct non supporté.");
                return;
            }

            _ = _wifiService.StartPublishingAsync();
            //_wifiService.StartSubscribingAsync(CancellationToken.None);
            MessageText = "Service Wi‑Fi Direct prêt.";
        }
        catch (Exception ex)
        {
            MessageText = "Initialisation impossible : " + ex.Message;
            SetState(TransferWorkflowState.Error, "Initialisation impossible : " + ex.Message);
        }
    }

    // Lance une recherche des appareils Wi‑Fi Direct disponibles autour de l'ordinateur.
    private void StartDiscovery()
    {
        SetState(TransferWorkflowState.DiscoveringPeers, "Recherche des appareils disponibles...");
        _ctsWatcher?.Cancel();
        DiscoveredDevices.Clear();
        _ctsWatcher = new CancellationTokenSource();
        _wifiService.StartSubscribingAsync(_ctsWatcher.Token);
    }

    // Met à jour l'état du workflow et le message principal affiché à l'utilisateur.
    private void SetState(TransferWorkflowState state, string? statusMessage = null)
    {
        CurrentState = state;
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            MessageText = statusMessage;
        }
        OnPropertyChanged(nameof(CurrentTransferStatusText));
        ((RelayCommand)CancelTransferCommand).RaiseCanExecuteChanged();
    }

    // Formate une taille en octets dans une unité lisible pour l'interface.
    private static string FormatSize(long size)
    {
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        double value = size;
        int index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }

    // Retourne le dossier de téléchargement par défaut utilisé pour les fichiers reçus.
    private static string GetDefaultDownloadFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Wi-Fi P2P");

    // Ouvre le dossier de destination dans l'explorateur Windows.
    private void OpenDownloadFolder()
    {
        string? targetFolder = string.IsNullOrWhiteSpace(DownloadFolder) ? GetDefaultDownloadFolder() : DownloadFolder;
        Directory.CreateDirectory(targetFolder);
        Process.Start(new ProcessStartInfo { FileName = targetFolder, UseShellExecute = true });
    }

    // Efface l'historique complet des transferts déjà enregistrés.
    private void ClearTransferHistory()
    {
        RecentTransfers.Clear();
        OnPropertyChanged(nameof(HasRecentTransfers));
    }

    // Ajoute un élément à l'historique récent avec son statut et son chemin de destination.
    private void AppendTransferHistory(string senderName, string type, string status, string destinationPath, long size)
    {
        RecentTransfers.Insert(0, new TransferHistoryEntry
        {
            Name = senderName,
            Type = type,
            Status = status,
            Path = destinationPath,
            Size = size,
            Timestamp = DateTime.Now
        });

        while (RecentTransfers.Count > 8)
        {
            RecentTransfers.RemoveAt(RecentTransfers.Count - 1);
        }

        OnPropertyChanged(nameof(HasRecentTransfers));
        ((RelayCommand)ClearTransferHistoryCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Ouvre la boîte de dialogue pour choisir des fichiers ou dossiers à ajouter à la liste d'envoi.
    /// </summary>
    private async Task ExecuteBrowseAsync()
    {
        // Because ViewModels shouldn't show dialogs directly, use a simple OpenFileDialog as a pragmatic choice here
        // This keeps parity with previous behaviour; for a stricter MVVM approach an IDialogService should be injected.
        OpenFileDialog? dialog = new() { CheckFileExists = true, Multiselect = true, Title = "Select File(s) or Folder(s)", DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog() is true)
        {
            AddToSender(dialog.FileNames);
        }
        await Task.CompletedTask;
    }
    /// <summary>
    /// Redémarre le service Wi‑Fi Direct afin de réinitialiser l'état de découverte et de connexion.
    /// </summary>
    /// <returns></returns>
    private void RetryPage()
    {
        try
        {
            _wifiService.Stop();
            // Re-init
            _wifiService.InitializeAsync().Wait();
            if (!_wifiService.IsHardwareSupported)
            {
                _wifiService.Stop();
                MessageText = "Hardware Wi-Fi Direct non supporté";
                return;
            }

            _wifiService.StartPublishingAsync();
            Reload();
        }
        catch (Exception ex)
        {
            MessageText = ex.Message;
        }
    }

    // Relance la découverte des pairs disponibles autour de l'appareil.
    private void Reload()
    {
        StartDiscovery();
    }

    // Efface tous les fichiers en attente et remet le workflow dans son état initial.
    private void DeleteAll()
    {
        PendingFiles.Clear();
        UpdateDropStatus();
        SetState(TransferWorkflowState.Idle, "Aucun élément sélectionné. Ajoutez un fichier ou un dossier.");
        Reload();
    }

    // Retire un fichier précis de la liste des éléments à envoyer.
    private void DeleteItem(FileI? file)
    {
        if (file == null) return;
        PendingFiles.Remove(file);
        UpdateDropStatus();
        if (PendingFiles.Count == 0)
        {
            SetState(TransferWorkflowState.Idle, "Aucun élément sélectionné. Ajoutez un fichier ou un dossier.");
        }
    }

    // Tente d'établir la connexion avec un appareil cible avant de lancer le transfert.
    private async Task ConnectAndSendAsync(WifiDirectPeer? target)
    {
        if (target == null) return;
        if (PendingFiles.Count == 0)
        {
            MessageBox.Show($"Veuillez d'abord glisser-déposer des éléments. avant d'envoyer a {target.Name}");
            return;
        }

        try
        {
            SelectedPeerName = target.Name;
            SetState(TransferWorkflowState.PeerSelected, $"Appareil sélectionné : {target.Name}");
            SetState(TransferWorkflowState.PairingRequested, "Demande de connexion en cours...");

            ProgressText = "Demande de connection en cours...";
            _ctsInvitation = new();
            _ctsConction = new();
            if (target.TestMode) { ProgressText = "Test reçu avec success "; return; }

            if (string.IsNullOrWhiteSpace(target.Id))
            {
                throw new InvalidOperationException("L'appareil sélectionné n'a pas d'identifiant valide.");
            }

            await WaitConnectionEstablishAsync(target);
            await _wifiService.ConnectToPeerAsync(target, _ctsConction.Token);
            SetState(TransferWorkflowState.TransferNegotiation, "Connexion établie. Négociation du transfert...");
        }
        catch (Exception ex)
        {
            SetState(TransferWorkflowState.Error, "Erreur : " + ex.Message);
            ProgressText = "Erreur : " + ex.Message;
        }
    }

    // Attend la confirmation de connexion avec un pair, sans bloquer l'interface utilisateur.
    private Task WaitConnectionEstablishAsync(WifiDirectPeer targetDevice)
    {
        PeerWaiting = targetDevice;
        // Save/flag logic can be implemented if needed; previously used an event Sended — for simplicity, rely on existing ConnectionEstablished handler
        return Task.CompletedTask;
    }

    // Ajoute plusieurs chemins de fichiers ou dossiers à la liste des éléments à envoyer.
    public void AddFiles(IEnumerable<string>? files)
    {
        if (files == null)
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            var added = 0;
            foreach (var rawPath in files.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var candidate = rawPath.Trim();
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    continue;
                }

                var normalized = Path.GetFullPath(candidate);
                if (PendingFiles.Any(item => string.Equals(item.PathFile, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                PendingFiles.Add(new FileI(normalized));
                added++;
            }

            UpdateDropStatus();
            if (added > 0)
            {
                SetState(TransferWorkflowState.PeerSelected, $"{PendingFiles.Count} élément(s) préparé(s). Sélectionnez un appareil.");
            }
            else
            {
                MessageText = "Aucun élément valide n'a été ajouté.";
            }
        });
    }

    // Enchaîne l'ajout de fichiers à partir d'un tableau de chemins de sélection.
    private void AddToSender(string[] files)
    {
        AddFiles(files);
    }

    // Met à jour le texte de statut visuel pour refléter le nombre d'éléments prêts à l'envoi.
    private void UpdateDropStatus()
    {
        DropStatusText = PendingFiles.Count == 0 ? "Aucun élément sélectionné" : $"{PendingFiles.Count} élément(s) prêt(s). Sélectionnez un appareil en bas.";
    }

    // Met à jour le texte de statut général lorsque le service Wi‑Fi Direct émet un changement.
    private void WifiService_StatusChanged(object? sender, WifiDirectStatusEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() => MessageText = e.Status.ToString());
    }

    // Ajoute un appareil détecté à la liste si ce n'est pas déjà présent.
    private void WifiService_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e)
        => Application.Current.Dispatcher.Invoke(() =>
        {
            if (DiscoveredDevices.Contains(e.Peer)) return;
            DiscoveredDevices.Add(e.Peer);
            SetState(TransferWorkflowState.DiscoveringPeers, $"{DiscoveredDevices.Count} appareil(s) détecté(s).");
        });

    // Rafraîchit les informations d'un périphérique déjà connu après sa mise à jour.
    private void WifiService_PeerUpdated(object? sender, WifiDirectPeerEventArgs e)
        => Application.Current.Dispatcher.Invoke(() => RefreshPeer(e.Peer));

    // Supprime un périphérique de la liste lorsqu'il n'est plus disponible.
    private void WifiService_PeerRemoved(object? sender, WifiDirectPeerEventArgs e)
        => Application.Current.Dispatcher.Invoke(() =>
        {
            RemovePeer(e.Peer);
            if (DiscoveredDevices.Count == 0)
            {
                SetState(TransferWorkflowState.Idle, "Aucun appareil détecté. Relancez une recherche.");
            }
        });

    // Répond à une invitation reçue en demandant à l'utilisateur s'il accepte la connexion.
    private async void WifiService_InvitationReceived(object? sender, WifiDirectInvitationEventArgs e)
    {
        _ = await Application.Current.Dispatcher.InvokeAsync(async () => await HandleInvitationAsync(e));
    }

    // Traite une invitation de connexion en acceptant ou refusant selon le choix de l'utilisateur.
    private async Task HandleInvitationAsync(WifiDirectInvitationEventArgs invitation)
    {
        var result = MessageBox.Show($"{invitation.Request.DeviceInformation.Name} souhaite établir une connexion Wi-Fi Direct.", "Demande de connexion", MessageBoxButton.YesNo, MessageBoxImage.Question);
        try
        {
            if (result == MessageBoxResult.Yes)
            {
                await _wifiService.AcceptInvitationAsync(invitation.Token);
                MessageText = "Demande acceptée.";
            }
            else
            {
                await _wifiService.RejectInvitationAsync(invitation.Token);
                MessageText = "Demande refusée.";
            }
        }
        catch (Exception ex)
        {
            MessageText = "Traitement de la demande impossible : " + ex.Message;
        }
    }

    // S'exécute dès qu'une connexion Wi‑Fi Direct est établie pour lancer l'écoute des transferts.
    private void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(async () =>
        {
            SetState(TransferWorkflowState.TransferNegotiation, "Connexion Wi‑Fi Direct établie.");

            // Configure transfer endpoint
            var localIp = e.LocalIpAddress;
            const int localPort = 5050;
            _ = _transferService.StartListeningAsync(localIp, localPort, CancellationToken.None);
        });
    }

    // Nettoie les ressources liées à une connexion fermée.
    private void WifiService_ConnectionClosed(object? sender, WifiDirectConnectionEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // clear any runtime IPs if desired
        });
    }

    // Affiche la demande de transfert entrante et prépare l'interface pour l'acceptation ou le refus.
    private void OnIncomingTransferRequested(object? sender, TransferSessionMetadata metadata)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IncomingTransferSession = metadata;
            SetState(TransferWorkflowState.TransferNegotiation, $"Demande de réception de {metadata.SenderName}.");
            ProgressText = $"Réception : {metadata.TotalFiles} élément(s) - {FormatSize(metadata.TotalSize)}";
            OnPropertyChanged(nameof(TransferRequestSummary));
        });
    }

    // Accepte une demande de transfert entrante et lance le téléchargement dans le dossier cible.
    private async Task AcceptIncomingTransferAsync()
    {
        if (IncomingTransferSession == null)
        {
            return;
        }

        var metadata = IncomingTransferSession;
        try
        {
            var decision = await _transferService.AcceptTransferAsync(metadata, DownloadFolder, CancellationToken.None);
            metadata.NegotiationStatus = TransferNegotiationStatus.Accepted;
            metadata.SessionState = TransferSessionState.ReadyToSend;
            metadata.TargetFolder = decision.DestinationFolder;

            _ctsTransfert = new();
            string destPath = string.IsNullOrWhiteSpace(decision.DestinationFolder)
                ? DownloadFolder
                : decision.DestinationFolder;
            DownloadFolder = destPath;
            Directory.CreateDirectory(destPath);

            var progress = new Progress<FileTransferProgress>(p =>
            {
                TransferProgress = p.Percentage;
                SetState(TransferWorkflowState.Receiving, $"Réception : {p.CurrentFileName} ({p.Percentage:0.0}%)");
                ProgressText = $"Réception : {p.CurrentFileName} ({p.Percentage:0.0}%)";
            });

            await _transferService.DownloadSessionAsync(metadata, destPath, progress, _ctsTransfert.Token);
            AppendTransferHistory(metadata.SenderName, "Réception", "Terminé", destPath, metadata.TotalSize);
            SetState(TransferWorkflowState.Completed, "Transfert terminé avec succès !");
            ProgressText = "Transfert terminé avec succès !";
            ClearTransferRequest();
        }
        catch (Exception ex)
        {
            AppendTransferHistory(metadata.SenderName, "Réception", "Erreur", DownloadFolder, metadata.TotalSize);
            SetState(TransferWorkflowState.Error, "Erreur de téléchargement : " + ex.Message);
            ProgressText = "Erreur de téléchargement : " + ex.Message;
        }
    }

    // Refuse une demande de transfert entrante et enregistre ce refus dans l'historique.
    private void RejectIncomingTransfer()
    {
        if (IncomingTransferSession == null)
        {
            return;
        }

        var metadata = IncomingTransferSession;
        IncomingTransferSession.NegotiationStatus = TransferNegotiationStatus.Refused;
        IncomingTransferSession.SessionState = TransferSessionState.Cancelled;
        AppendTransferHistory(metadata.SenderName, "Réception", "Refusé", DownloadFolder, metadata.TotalSize);
        SetState(TransferWorkflowState.Cancelled, "Transfert refusé par l’utilisateur.");
        ClearTransferRequest();
    }

    // Annule un transfert actif en cours de traitement et nettoie l'état associé.
    private void CancelActiveTransfer()
    {
        _ctsTransfert?.Cancel();
        if (IncomingTransferSession != null)
        {
            IncomingTransferSession.NegotiationStatus = TransferNegotiationStatus.Cancelled;
            IncomingTransferSession.SessionState = TransferSessionState.Cancelled;
            AppendTransferHistory(IncomingTransferSession.SenderName, "Réception", "Annulé", DownloadFolder, IncomingTransferSession.TotalSize);
        }

        SetState(TransferWorkflowState.Cancelled, "Transfert annulé par l’utilisateur.");
        ClearTransferRequest();
    }

    // Libère l'état de la demande de transfert et réinitialise la progression.
    private void ClearTransferRequest()
    {
        IncomingTransferSession = null;
        TransferProgress = 0;
        OnPropertyChanged(nameof(IsTransferRequestVisible));
        OnPropertyChanged(nameof(TransferRequestSummary));
        ((RelayCommand)AcceptTransferCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RejectTransferCommand).RaiseCanExecuteChanged();
    }

    // Met à jour les informations d'un pair déjà connu ou l'ajoute s'il est nouveau.
    private void RefreshPeer(WifiDirectPeer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var value = DiscoveredDevices.FirstOrDefault(p => p.Id == peer.Id);
            if (value != null)
            {
                var idx = DiscoveredDevices.IndexOf(value);
                DiscoveredDevices[idx] = peer;
            }
            else
            {
                peer.Name = "Inconnu";
                DiscoveredDevices.Add(peer);
            }

            if (!string.IsNullOrWhiteSpace(peer.Name))
            {
                SelectedPeerName = peer.Name;
            }
        });
    }

    // Supprime un périphérique de la liste des appareils détectés.
    private void RemovePeer(WifiDirectPeer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var value = DiscoveredDevices.FirstOrDefault(p => p.Id == peer.Id);
            if (value != null)
            {
                DiscoveredDevices.Remove(value);
            }
        });
    }

    // Notifie les abonnés qu'une propriété a été modifiée pour mettre à jour l'interface.
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Minimal RelayCommand implementation
    private class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        private readonly Action<object?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        private readonly Predicate<object?>? _canExecute = canExecute;

        // Vérifie si la commande est exécutable selon le contexte courant.
        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);
        public event EventHandler? CanExecuteChanged;

        // Exécute l'action associée à la commande.
        public void Execute(object? parameter) => _execute(parameter);

        // Force le recalcul du statut d'exécution de la commande.
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
