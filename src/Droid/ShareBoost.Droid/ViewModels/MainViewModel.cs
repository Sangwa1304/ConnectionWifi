using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ShareBoost.Droid.Infrastructure;
using ShareBoost.Droid.Models;
using ShareBoost.Droid.Platforms.Android;

namespace ShareBoost.Droid.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AndroidWifiDirectService _wifi;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _operation;
    private AndroidWifiDirectPeer? _selectedPeer;
    private AndroidWifiDirectConnection? _connection;
    private string _status = "Initialisation requise";
    private string _message = "Activez le Wi-Fi, puis lancez la recherche d'appareils.";
    private string _error = string.Empty;
    private bool _isBusy;
    private bool _isInitialized;
    private double _transferProgress;
    private bool _disposed;

    public ObservableCollection<AndroidWifiDirectPeer> Peers { get; } = [];
    public ObservableCollection<TransferFileItem> SelectedFiles { get; } = [];

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseCommands(); } }
    public bool IsInitialized { get => _isInitialized; private set { if (Set(ref _isInitialized, value)) RaiseCommands(); } }
    public bool IsConnected => _connection?.GroupFormed == true;
    public string ConnectionRole => _connection is null ? "Aucune session" : _connection.IsGroupOwner ? "Hôte du groupe Wi-Fi Direct" : "Client Wi-Fi Direct";
    public string GroupOwnerAddress => _connection?.GroupOwnerAddress ?? "Adresse réseau non communiquée";
    public string SelectedPeerName => _selectedPeer?.Name ?? "Aucun appareil sélectionné";
    public bool HasSelectedFiles => SelectedFiles.Count > 0;
    public string SelectedFilesSummary => $"{SelectedFiles.Count} fichier(s) · {FormatSize(SelectedFiles.Sum(x => x.Length))}";
    public double TransferProgress { get => _transferProgress; private set => Set(ref _transferProgress, value); }
    public bool CanSend => IsConnected && HasSelectedFiles && !IsBusy;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(AndroidWifiDirectService wifi)
    {
        _wifi = wifi;
        OpenWifiSettingsCommand = new AsyncCommand(_ => { _wifi.OpenWifiSettings(); return Task.CompletedTask; }, _ => !IsBusy);
        InitializeCommand = new AsyncCommand(_ => InitializeAsync(), _ => !IsInitialized && !IsBusy);
        DiscoverCommand = new AsyncCommand(_ => DiscoverAsync(), _ => IsInitialized && !IsBusy);
        PublishCommand = new AsyncCommand(_ => PublishAsync(), _ => IsInitialized && !IsBusy);
        ConnectCommand = new AsyncCommand(p => ConnectAsync(p as AndroidWifiDirectPeer), p => IsInitialized && !IsBusy && p is AndroidWifiDirectPeer);
        DisconnectCommand = new AsyncCommand(_ => DisconnectAsync(), _ => IsConnected && !IsBusy);
        PickFilesCommand = new AsyncCommand(_ => PickFilesAsync(), _ => !IsBusy);
        RemoveFileCommand = new AsyncCommand(p => RemoveFileAsync(p as TransferFileItem), p => !IsBusy && p is TransferFileItem);
        ClearFilesCommand = new AsyncCommand(_ =>
        {
            foreach (var file in SelectedFiles.ToArray())
                try { Directory.Delete(Path.GetDirectoryName(file.LocalPath)!, true); } catch (IOException) { }
            SelectedFiles.Clear();
            NotifyFilesChanged();
            return Task.CompletedTask;
        }, _ => SelectedFiles.Count > 0 && !IsBusy);
        // The send action is deliberately not wired to a placeholder: the current
        // Windows TCP framing is not yet a cross-platform, negotiated protocol.
        SendCommand = new AsyncCommand(_ => Task.CompletedTask, _ => false);

        _wifi.StatusChanged += Wifi_StatusChanged;
        _wifi.PeerDiscovered += Wifi_PeerDiscovered;
        _wifi.PeerUpdated += Wifi_PeerUpdated;
        _wifi.PeerRemoved += Wifi_PeerRemoved;
        _wifi.ConnectionEstablished += Wifi_ConnectionEstablished;
        _wifi.Error += Wifi_Error;
    }

    private async Task InitializeAsync()
    {
        await RunExclusiveAsync(async token =>
        {
            Error = string.Empty;
            Message = "Vérification des autorisations Android…";
            IsInitialized = await _wifi.InitializeAsync(token);
            Status = IsInitialized ? "Wi-Fi Direct prêt" : "Autorisation ou service indisponible";
            Message = IsInitialized ? "Vous pouvez rechercher un appareil ou publier ce téléphone." : "Vérifiez les autorisations Wi-Fi à proximité dans les paramètres Android.";
        });
    }

    private Task DiscoverAsync() => RunExclusiveAsync(async token =>
    {
        Error = string.Empty;
        Message = "Recherche des appareils Wi-Fi Direct…";
        bool started = await _wifi.StartSubscribingAsync(token);
        if (started) Message = "Recherche lancée. Les appareils compatibles apparaîtront ici.";
    });

    private Task PublishAsync() => RunExclusiveAsync(async token =>
    {
        Error = string.Empty;
        Message = "Publication du groupe Wi-Fi Direct…";
        bool started = await _wifi.StartPublishingAsync(token);
        if (started) Message = "Ce téléphone est disponible comme hôte. Les demandes entrantes restent soumises à la confirmation Android.";
    });

    private Task ConnectAsync(AndroidWifiDirectPeer? peer) => RunExclusiveAsync(async token =>
    {
        if (peer is null) return;
        Error = string.Empty;
        _selectedPeer = peer;
        OnPropertyChanged(nameof(SelectedPeerName));
        Message = $"Demande de connexion à {peer.Name}. Confirmez la demande dans l'interface système Android si elle apparaît.";
        var connection = await _wifi.ConnectToPeerAsync(peer, token);
        if (connection?.GroupFormed == true)
        {
            _connection = connection;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectionRole));
            OnPropertyChanged(nameof(GroupOwnerAddress));
            Message = "Groupe Wi-Fi Direct établi. Le canal de transfert de fichiers n'est pas encore négocié avec le protocole Windows.";
        }
        else Message = "La connexion n'a pas été établie.";
        RaiseCommands();
    });

    private Task DisconnectAsync() => RunExclusiveAsync(async _ =>
    {
        await _wifi.DisconnectAsync();
        _connection = null;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionRole));
        OnPropertyChanged(nameof(GroupOwnerAddress));
        Message = "Session Wi-Fi Direct terminée.";
        RaiseCommands();
    });

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
                await using var input = await file.OpenReadAsync();
                var directory = Path.Combine(FileSystem.CacheDirectory, "outgoing", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var safeName = Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "fichier";
                var localPath = Path.Combine(directory, safeName);
                await using (var output = File.Create(localPath))
                    await input.CopyToAsync(output);

                var info = new FileInfo(localPath);
                ImageSource? thumbnail = null;
                string extension = info.Extension.ToLowerInvariant();
                string kind = extension switch
                {
                    ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".heic" => "Image",
                    ".mp4" or ".mov" or ".mkv" or ".webm" => "Vidéo",
                    ".mp3" or ".wav" or ".m4a" or ".flac" => "Audio",
                    ".pdf" => "PDF",
                    _ => "Fichier"
                };
                if (kind == "Image")
                    thumbnail = ImageSource.FromFile(localPath);

                if (SelectedFiles.All(x => !string.Equals(x.LocalPath, localPath, StringComparison.OrdinalIgnoreCase)))
                    SelectedFiles.Add(new TransferFileItem(safeName, localPath, info.Length, thumbnail, kind));
            }

            NotifyFilesChanged();
            Message = SelectedFiles.Count == 0 ? "Aucun fichier sélectionné." : "Fichiers prêts dans la file d'envoi.";
        }
        catch (Exception ex) { Error = $"Sélection de fichiers impossible : {ex.Message}"; }
    }

    private Task RemoveFileAsync(TransferFileItem? item)
    {
        if (item is not null)
        {
            SelectedFiles.Remove(item);
            try { Directory.Delete(Path.GetDirectoryName(item.LocalPath)!, true); } catch (IOException) { }
            NotifyFilesChanged();
        }
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

    private void Wifi_StatusChanged(object? sender, AndroidWifiDirectStatus status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Status = status.ToString();
            if (status != AndroidWifiDirectStatus.Connected) _connection = null;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectionRole));
            OnPropertyChanged(nameof(GroupOwnerAddress));
            RaiseCommands();
        });
    }

    private void Wifi_PeerDiscovered(object? sender, AndroidWifiDirectPeer peer) => MainThread.BeginInvokeOnMainThread(() =>
    {
        var existing = Peers.FirstOrDefault(x => x.Address == peer.Address);
        if (existing is null) Peers.Add(peer);
        else
        {
            var index = Peers.IndexOf(existing);
            Peers[index] = peer;
        }
    });

    private void Wifi_PeerUpdated(object? sender, AndroidWifiDirectPeer peer) => MainThread.BeginInvokeOnMainThread(() =>
    {
        var existing = Peers.FirstOrDefault(x => x.Address == peer.Address);
        if (existing is null) Peers.Add(peer);
        else ReplacePeer(existing, peer);
    });

    private void Wifi_PeerRemoved(object? sender, AndroidWifiDirectPeer peer) => MainThread.BeginInvokeOnMainThread(() =>
    {
        var existing = Peers.FirstOrDefault(x => x.Address == peer.Address);
        if (existing is not null) Peers.Remove(existing);
    });

    private void Wifi_ConnectionEstablished(object? sender, AndroidWifiDirectConnection connection) => MainThread.BeginInvokeOnMainThread(() =>
    {
        _connection = connection;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionRole));
        OnPropertyChanged(nameof(GroupOwnerAddress));
        Message = "Connexion Wi-Fi Direct confirmée.";
        RaiseCommands();
    });

    private void Wifi_Error(object? sender, string message) => MainThread.BeginInvokeOnMainThread(() => Error = message);

    private void NotifyFilesChanged()
    {
        OnPropertyChanged(nameof(HasSelectedFiles));
        OnPropertyChanged(nameof(SelectedFilesSummary));
        OnPropertyChanged(nameof(CanSend));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { OpenWifiSettingsCommand, InitializeCommand, DiscoverCommand, PublishCommand, ConnectCommand, DisconnectCommand, PickFilesCommand, RemoveFileCommand, ClearFilesCommand, SendCommand })
            (command as AsyncCommand)?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSend));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
        _wifi.StatusChanged -= Wifi_StatusChanged;
        _wifi.PeerDiscovered -= Wifi_PeerDiscovered;
        _wifi.PeerUpdated -= Wifi_PeerUpdated;
        _wifi.PeerRemoved -= Wifi_PeerRemoved;
        _wifi.ConnectionEstablished -= Wifi_ConnectionEstablished;
        _wifi.Error -= Wifi_Error;
        _operationGate.Dispose();
    }
}
