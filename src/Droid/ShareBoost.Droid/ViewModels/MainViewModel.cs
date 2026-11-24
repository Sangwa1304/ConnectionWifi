using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LibraryMI.Models;
using ServiceTcpChared;
using ShareBoost.Droid.Infrastructure;
using ShareBoost.Droid.Models;
using ShareBoost.Droid.Platforms.Android;
using ShareBoost.Droid.Services;
using WindowsWifiLibrary.Interfaces;
using WindowsWifiLibrary.Models;

namespace ShareBoost.Droid.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IWifiDirectService _wifi;
    private readonly AndroidWifiDirectService _androidNative;
    private readonly AndroidTransferOrchestrator _transfer = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _networkSession;
    private CancellationTokenSource? _transferCancellation;
    private TaskCompletionSource<bool>? _networkReady;
    private WifiDirectPeer? _selectedPeer;
    private WifiDirectConnectionEventArgs? _connection;
    private string _status = "Initialisation requise";
    private string _message = "Initialisez le Wi-Fi Direct pour commencer.";
    private string _error = string.Empty;
    private bool _isBusy;
    private bool _isInitialized;
    private bool _isTransferBusy;
    private double _transferProgress;
    private bool _disposed;

    public ObservableCollection<WifiDirectPeer> Peers { get; } = [];
    public ObservableCollection<TransferFileItem> SelectedFiles { get; } = [];
    public ObservableCollection<TransferHistoryEntry> RecentTransfers { get; } = [];
    public ObservableCollection<TransferFileItem> ReceivedFiles { get; } = [];

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseCommands(); } }
    public bool IsInitialized { get => _isInitialized; private set { if (Set(ref _isInitialized, value)) RaiseCommands(); } }
    public bool IsConnected => _wifi.CurrentStatus == WifiDirectStatus.Connected;
    public bool IsTransferReady => _transfer.IsRunning;
    public string ConnectionRole => _connection is null ? "Aucune session" : _connection.ServerMode ? "Initiateur — canaux TCP actifs" : "Répondant — canaux TCP actifs";
    public string RemoteAddress => _connection?.RemoteIpAddress ?? "Adresse distante non résolue";
    public string SelectedPeerName => _selectedPeer?.Name ?? "Aucun appareil sélectionné";
    public bool HasSelectedFiles => SelectedFiles.Count > 0;
    public string SelectedFilesSummary => $"{SelectedFiles.Count} fichier(s) · {FormatSize(SelectedFiles.Sum(x => x.Length))}";
    public double TransferProgress { get => _transferProgress; private set => Set(ref _transferProgress, Math.Clamp(value, 0, 1)); }
    public string TransferStatus => _transfer.IsRunning
        ? $"Envoi : {_transfer.SendDescription} · Réception : {_transfer.ReceiveDescription}"
        : "Canaux TCP en attente";
    public bool CanSend => IsConnected && IsTransferReady && HasSelectedFiles && !_isTransferBusy && !IsBusy;

    public ICommand OpenWifiSettingsCommand { get; }
    public ICommand InitializeCommand { get; }
    public ICommand DiscoverCommand { get; }
    public ICommand PublishCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand PickFilesCommand { get; }
    public ICommand RemoveFileCommand { get; }
    public ICommand ClearFilesCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CancelTransferCommand { get; }
    public ICommand OpenReceivedFileCommand { get; }
    public ICommand ShareReceivedFileCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Func<InfoMetas, Task<bool>>? IncomingTransferRequested;

    public MainViewModel(IWifiDirectService wifi, AndroidWifiDirectService androidNative)
    {
        _wifi = wifi;
        _androidNative = androidNative;
        StreamingData.DownloadDirectory = Path.Combine(FileSystem.AppDataDirectory, "Received");
        Directory.CreateDirectory(StreamingData.DownloadDirectory);
        OpenWifiSettingsCommand = new AsyncCommand(_ => { _androidNative.OpenWifiSettings(); return Task.CompletedTask; }, _ => !IsBusy);
        InitializeCommand = new AsyncCommand(_ => InitializeAsync(), _ => !IsInitialized && !IsBusy);
        DiscoverCommand = new AsyncCommand(_ => DiscoverAsync(), _ => IsInitialized && !IsBusy);
        PublishCommand = new AsyncCommand(_ => PublishAsync(), _ => IsInitialized && !IsBusy);
        ConnectCommand = new AsyncCommand(p => ConnectAsync(p as WifiDirectPeer), p => IsInitialized && !IsBusy && p is WifiDirectPeer);
        DisconnectCommand = new AsyncCommand(_ => DisconnectAsync(), _ => IsConnected && !IsBusy);
        PickFilesCommand = new AsyncCommand(_ => PickFilesAsync(), _ => !IsBusy && !_isTransferBusy);
        RemoveFileCommand = new AsyncCommand(p => RemoveFileAsync(p as TransferFileItem), p => !IsBusy && !_isTransferBusy && p is TransferFileItem);
        ClearFilesCommand = new AsyncCommand(_ => ClearFilesAsync(), _ => SelectedFiles.Count > 0 && !IsBusy && !_isTransferBusy);
        SendCommand = new AsyncCommand(_ => SendAsync(), _ => CanSend);
        CancelTransferCommand = new AsyncCommand(_ => CancelTransferAsync(), _ => _isTransferBusy);
        OpenReceivedFileCommand = new AsyncCommand(p => OpenReceivedFileAsync(p as TransferFileItem), p => p is TransferFileItem);
        ShareReceivedFileCommand = new AsyncCommand(p => ShareReceivedFileAsync(p as TransferFileItem), p => p is TransferFileItem);

        _wifi.StatusChanged += Wifi_StatusChanged;
        _wifi.PublicherStatusChanged += Wifi_PublisherStatusChanged;
        _wifi.WatcherStatusChanged += Wifi_WatcherStatusChanged;
        _wifi.PeerDiscovered += Wifi_PeerDiscovered;
        _wifi.PeerUpdated += Wifi_PeerUpdated;
        _wifi.PeerRemoved += Wifi_PeerRemoved;
        _wifi.ConnectionEstablished += Wifi_ConnectionEstablished;
        _wifi.ConnectionClosed += Wifi_ConnectionClosed;
        _wifi.Error += Wifi_Error;
        _transfer.IncomingTransferRequested += Transfer_IncomingTransferRequested;
        _transfer.ProgressChanged += Transfer_ProgressChanged;
        _transfer.FilesChanged += Transfer_FilesChanged;
        _transfer.FileTransferCompleted += Transfer_FileTransferCompleted;
        RefreshReceivedFiles();
        SelectedFiles.CollectionChanged += (_, _) => NotifyFilesChanged();
        RecentTransfers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(RecentTransfers));
    }

    private async Task InitializeAsync()
    {
        await RunExclusiveAsync(async token =>
        {
            Message = "Vérification des permissions et du service Wi-Fi Direct Android…";
            IsInitialized = await _wifi.InitializeAsync(token);
            if (!IsInitialized)
            {
                Status = "Wi-Fi Direct indisponible";
                return;
            }
            Status = "Wi-Fi Direct prêt";
            Message = "Le service est prêt. Lancez une recherche ou publiez ce téléphone.";
            // Windows runs publisher and watcher together. Android's P2P API has
            // mutually exclusive discovery/group-owner operating states; retain
            // the same public contract while starting peer discovery here.
            await _wifi.StartSubscribingAsync(token);
        });
    }

    private Task DiscoverAsync() => RunExclusiveAsync(async token =>
    {
        Error = string.Empty;
        Message = "Recherche des appareils Wi-Fi Direct…";
        await _wifi.StartSubscribingAsync(token);
        Message = "Recherche lancée. Les appareils détectés apparaîtront ici.";
    });

    private Task PublishAsync() => RunExclusiveAsync(async token =>
    {
        Error = string.Empty;
        Message = "Création du groupe Wi-Fi Direct…";
        await _wifi.StartPublishingAsync(token);
        Message = "Ce téléphone est publié comme propriétaire du groupe. Les négociations entrantes restent contrôlées par Android.";
    });

    private async Task ConnectAsync(WifiDirectPeer? peer)
    {
        if (peer is null || _disposed) return;
        if (!await _operationGate.WaitAsync(0)) return;
        _operation = new CancellationTokenSource();
        IsBusy = true;
        Error = string.Empty;
        _selectedPeer = peer;
        OnPropertyChanged(nameof(SelectedPeerName));
        _networkReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Message = $"Demande de connexion à {peer.Name}…";
            bool connected = await _wifi.ConnectToPeerAsync(peer, _operation.Token);
            if (!connected)
            {
                Message = "La connexion Wi-Fi Direct n'a pas abouti.";
                return;
            }
            Message = "Groupe Wi-Fi Direct établi. Négociation des deux canaux TCP…";
            await _networkReady.Task.WaitAsync(TimeSpan.FromSeconds(75), _operation.Token);
            Message = "Connexion et canaux TCP établis. Vous pouvez envoyer ou recevoir des fichiers.";
        }
        catch (OperationCanceledException) { Message = "Connexion annulée."; }
        catch (Exception ex) { Error = ex.Message; Message = "La connexion ou l'initialisation TCP a échoué."; }
        finally
        {
            _operation.Dispose();
            _operation = null;
            IsBusy = false;
            _operationGate.Release();
            RaiseCommands();
        }
    }

    private async Task DisconnectAsync()
    {
        WifiDirectPeer? peer = _selectedPeer;
        _networkSession?.Cancel();
        _transfer.Stop();
        _wifi.DisconnectToPeer(ref peer, CancellationToken.None);
        _selectedPeer = null;
        _connection = null;
        OnPropertyChanged(nameof(SelectedPeerName));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionRole));
        OnPropertyChanged(nameof(RemoteAddress));
        Message = "Session Wi-Fi Direct et canaux TCP arrêtés.";
        RaiseCommands();
        await Task.CompletedTask;
    }

    private async Task PickFilesAsync()
    {
        try
        {
            var files = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Sélectionner les fichiers à partager"
            });
            if (files is null) return;

            foreach (var file in files)
            {
                var directory = Path.Combine(FileSystem.CacheDirectory, "outgoing", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var safeName = Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "fichier";
                var localPath = Path.Combine(directory, safeName);
                await using (var input = await file.OpenReadAsync())
                await using (var output = File.Create(localPath))
                    await input.CopyToAsync(output);

                var info = new FileInfo(localPath);
                string ext = info.Extension.ToLowerInvariant();
                string kind = ext switch
                {
                    ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".heic" => "Image",
                    ".mp4" or ".mov" or ".mkv" or ".webm" => "Vidéo",
                    ".mp3" or ".wav" or ".m4a" or ".flac" => "Audio",
                    ".pdf" => "PDF",
                    _ => "Fichier"
                };
                ImageSource? thumbnail = kind == "Image" ? ImageSource.FromFile(localPath) : null;
                SelectedFiles.Add(new TransferFileItem(safeName, localPath, info.Length, thumbnail, kind));
            }
            Message = $"{SelectedFiles.Count} fichier(s) prêt(s) à l'envoi.";
        }
        catch (Exception ex) { Error = $"Sélection de fichiers impossible : {ex.Message}"; }
    }

    private Task RemoveFileAsync(TransferFileItem? item)
    {
        if (item is null) return Task.CompletedTask;
        SelectedFiles.Remove(item);
        DeleteStagedFile(item);
        return Task.CompletedTask;
    }

    private Task ClearFilesAsync()
    {
        foreach (var item in SelectedFiles.ToArray()) DeleteStagedFile(item);
        SelectedFiles.Clear();
        return Task.CompletedTask;
    }

    private static void DeleteStagedFile(TransferFileItem item)
    {
        try { Directory.Delete(Path.GetDirectoryName(item.LocalPath)!, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task SendAsync()
    {
        if (!_transfer.IsRunning || SelectedFiles.Count == 0) return;
        if (!await _operationGate.WaitAsync(0)) return;
        _isTransferBusy = true;
        _transferCancellation?.Cancel();
        _transferCancellation?.Dispose();
        _transferCancellation = new CancellationTokenSource();
        RaiseCommands();
        try
        {
            var files = SelectedFiles.Select(f => new FileI(f.LocalPath)).ToArray();
            bool accepted = await _transfer.SendFilesAsync(files, _transferCancellation.Token);
            Message = accepted ? "Transfert terminé." : "Le destinataire a refusé le transfert.";
            if (accepted)
            {
                foreach (var file in SelectedFiles)
                    RecentTransfers.Insert(0, new TransferHistoryEntry { Name = file.Name, Type = "Envoi", Status = "Terminé", Path = file.LocalPath, Size = file.Length });
            }
        }
        catch (OperationCanceledException) { Message = "Transfert annulé."; }
        catch (Exception ex) { Error = $"Impossible de démarrer le transfert : {ex.Message}"; }
        finally
        {
            _isTransferBusy = false;
            _operationGate.Release();
            RaiseCommands();
        }
    }

    private async Task OpenReceivedFileAsync(TransferFileItem? item)
    {
        if (item is null || !File.Exists(item.LocalPath)) return;
        await Launcher.Default.OpenAsync(new OpenFileRequest(item.Name, item.LocalPath));
    }

    private async Task ShareReceivedFileAsync(TransferFileItem? item)
    {
        if (item is null || !File.Exists(item.LocalPath)) return;
        await Share.Default.RequestAsync(new ShareFileRequest($"Partager {item.Name}", new ShareFile(item.LocalPath)));
    }

    private void RefreshReceivedFiles()
    {
        if (!Directory.Exists(StreamingData.DownloadDirectory)) return;
        var paths = Directory.EnumerateFiles(StreamingData.DownloadDirectory)
            .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            foreach (var path in paths)
            {
                if (ReceivedFiles.Any(file => string.Equals(file.LocalPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                var info = new FileInfo(path);
                var ext = info.Extension.ToLowerInvariant();
                var kind = ext switch
                {
                    ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".heic" => "Image",
                    ".mp4" or ".mov" or ".mkv" or ".webm" => "Vidéo",
                    ".mp3" or ".wav" or ".m4a" or ".flac" => "Audio",
                    ".pdf" => "PDF",
                    _ => "Fichier"
                };
                ImageSource? thumbnail = kind == "Image" ? ImageSource.FromFile(path) : null;
                ReceivedFiles.Insert(0, new TransferFileItem(info.Name, path, info.Length, thumbnail, kind));
            }
            OnPropertyChanged(nameof(ReceivedFiles));
        });
    }

    private void Transfer_FileTransferCompleted(StreamingData.FileFinishe file)
    {
        if (file.Transfert == StreamingData.TransfertType.Recu)
            RefreshReceivedFiles();
        MainThread.BeginInvokeOnMainThread(() =>
            RecentTransfers.Insert(0, new TransferHistoryEntry
            {
                Name = file.Name,
                Type = file.Transfert == StreamingData.TransfertType.Recu ? "Réception" : "Envoi",
                Status = "Terminé",
                Path = StreamingData.DownloadDirectory,
                Size = file.Length,
                Timestamp = file.Time
            }));
    }

    private Task CancelTransferAsync()
    {
        _transferCancellation?.Cancel();
        Message = "Annulation du transfert demandée.";
        return Task.CompletedTask;
    }

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action)
    {
        if (!await _operationGate.WaitAsync(0)) return;
        _operation = new CancellationTokenSource();
        IsBusy = true;
        Error = string.Empty;
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Message = "Opération annulée."; }
        catch (Exception ex) { Error = ex.Message; Message = "L'opération a échoué."; }
        finally
        {
            _operation.Dispose();
            _operation = null;
            IsBusy = false;
            _operationGate.Release();
            RaiseCommands();
        }
    }

    private void Wifi_StatusChanged(object? sender, WifiDirectStatusEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Status = e.Status switch
            {
                WifiDirectStatus.Connected => "Connecté",
                WifiDirectStatus.Disconnected => "Déconnecté",
                WifiDirectStatus.Aborded => "Connexion interrompue",
                WifiDirectStatus.NoArdwareSupported => "Matériel non pris en charge",
                _ => "Initialisation"
            };
            OnPropertyChanged(nameof(IsConnected));
            RaiseCommands();
        });

    private void Wifi_PublisherStatusChanged(object? sender, WifiDirectPublicherStatusEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(Status)));

    private void Wifi_WatcherStatusChanged(object? sender, WifiDirectWatcherStatusEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(Status)));

    private void Wifi_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!Peers.Contains(e.Peer)) Peers.Add(e.Peer);
        });

    private void Wifi_PeerUpdated(object? sender, WifiDirectPeerEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var old = Peers.FirstOrDefault(p => p.Id == e.Peer.Id);
            if (old is null) Peers.Add(e.Peer);
            else Peers[Peers.IndexOf(old)] = e.Peer;
        });

    private void Wifi_PeerRemoved(object? sender, WifiDirectPeerEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var old = Peers.FirstOrDefault(p => p.Id == e.Peer.Id);
            if (old is not null) Peers.Remove(old);
        });

    private void Wifi_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _connection = e;
            OnPropertyChanged(nameof(ConnectionRole));
            OnPropertyChanged(nameof(RemoteAddress));
            Message = "Wi-Fi Direct établi. Initialisation du transport TCP Windows-compatible…";
            RaiseCommands();
        });
        _ = StartNetworkSessionAsync(e);
    }

    private async Task StartNetworkSessionAsync(WifiDirectConnectionEventArgs connection)
    {
        if (_disposed) return;
        _networkSession?.Cancel();
        _networkSession?.Dispose();
        _networkSession = new CancellationTokenSource();
        var token = _networkSession.Token;
        Exception? lastFailure = null;

        for (int attempt = 1; attempt <= 4 && !token.IsCancellationRequested; attempt++)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() => Message = attempt == 1 ? "Ouverture des deux canaux TCP…" : $"Rétablissement des canaux TCP ({attempt}/4)…");
                var run = _transfer.StartAsync(connection, token);
                Task ready = _transfer.WaitUntilReadyAsync(token);
                Task first = await Task.WhenAny(ready, run);
                if (first == run) await run;
                await ready;
                _networkReady?.TrySetResult(true);
                OnPropertyChanged(nameof(IsTransferReady));
                OnPropertyChanged(nameof(TransferStatus));
                RaiseCommands();
                await run;
                if (!token.IsCancellationRequested)
                    throw new IOException("La session TCP s'est fermée sans demande d'annulation.");
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                lastFailure = ex;
                if (attempt < 4 && !token.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
            }
        }

        if (lastFailure is not null)
        {
            _networkReady?.TrySetException(lastFailure);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Error = $"Les canaux TCP n'ont pas pu être établis : {lastFailure.Message}";
                OnPropertyChanged(nameof(IsTransferReady));
                RaiseCommands();
            });
        }
    }

    private Task<bool> Transfer_IncomingTransferRequested(InfoMetas metadata)
    {
        if (_disposed || IncomingTransferRequested is null)
            return Task.FromResult(false);
        return IncomingTransferRequested.Invoke(metadata);
    }

    private void Wifi_ConnectionClosed(object? sender, WifiDirectPeerEventArgs e)
    {
        _networkSession?.Cancel();
        _transfer.Stop();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _connection = null;
            Message = $"Connexion fermée : {e.Peer.Name}.";
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsTransferReady));
            OnPropertyChanged(nameof(ConnectionRole));
            RaiseCommands();
        });
    }

    private void Wifi_Error(object? sender, WifiDirectErrorEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() => Error = string.IsNullOrWhiteSpace(e.message) ? e.Error?.Message ?? "Erreur Wi-Fi Direct." : e.message);

    private void Transfer_ProgressChanged() => MainThread.BeginInvokeOnMainThread(() =>
    {
        TransferProgress = Math.Max(_transfer.SendProgress, _transfer.ReceiveProgress) / 100d;
        OnPropertyChanged(nameof(TransferStatus));
    });

    private void Transfer_FilesChanged() => MainThread.BeginInvokeOnMainThread(() =>
        OnPropertyChanged(nameof(TransferStatus)));

    private void NotifyFilesChanged()
    {
        OnPropertyChanged(nameof(HasSelectedFiles));
        OnPropertyChanged(nameof(SelectedFilesSummary));
        OnPropertyChanged(nameof(CanSend));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { OpenWifiSettingsCommand, InitializeCommand, DiscoverCommand, PublishCommand, ConnectCommand, DisconnectCommand, PickFilesCommand, RemoveFileCommand, ClearFilesCommand, SendCommand, CancelTransferCommand, OpenReceivedFileCommand, ShareReceivedFileCommand })
            (command as AsyncCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSend));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} o";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.##} Ko";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.##} Mo";
        return $"{bytes / (1024d * 1024 * 1024):0.##} Go";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operation?.Cancel();
        _networkSession?.Cancel();
        _transferCancellation?.Cancel();
        _transfer.Dispose();
        _wifi.StatusChanged -= Wifi_StatusChanged;
        _wifi.PublicherStatusChanged -= Wifi_PublisherStatusChanged;
        _wifi.WatcherStatusChanged -= Wifi_WatcherStatusChanged;
        _wifi.PeerDiscovered -= Wifi_PeerDiscovered;
        _wifi.PeerUpdated -= Wifi_PeerUpdated;
        _wifi.PeerRemoved -= Wifi_PeerRemoved;
        _wifi.ConnectionEstablished -= Wifi_ConnectionEstablished;
        _wifi.ConnectionClosed -= Wifi_ConnectionClosed;
        _wifi.Error -= Wifi_Error;
        _wifi.Dispose();
        _operation?.Dispose();
        _networkSession?.Dispose();
        _transferCancellation?.Dispose();
        _operationGate.Dispose();
    }
}
