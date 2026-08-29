using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using WindowsWifiLibrary.Core.Models;
using WindowsWifiLibrary.Core.Services;
using WindowsWifiLibrary.Models;
using WindowsWifiLibrary.Services;
using WindowsWifiWpf.Models;

namespace WindowsWifiWpf.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        // Services
        private readonly WindowsWifiDirectService _wifiService;
        private readonly FileTransferService _transferService;

        // State
        public ObservableCollection<WifiDirectPeer> DiscoveredDevices { get; } = new();
        public ObservableCollection<FileI> PendingFiles { get; } = new();

        private string _dropStatusText = "Aucun élément sélectionné";
        public string DropStatusText { get => _dropStatusText; set { _dropStatusText = value; OnPropertyChanged(nameof(DropStatusText)); } }

        private string _messageText = string.Empty;
        public string MessageText { get => _messageText; set { _messageText = value; OnPropertyChanged(nameof(MessageText)); } }

        private string _progressText = "En attente...";
        public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(nameof(ProgressText)); } }

        private double _transferProgress;
        public double TransferProgress { get => _transferProgress; set { _transferProgress = value; OnPropertyChanged(nameof(TransferProgress)); } }

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
        public ICommand DeleteAllCommand { get; }
        public ICommand DeleteItemCommand { get; }
        public ICommand ConnectAndSendCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel()
        {
            _wifiService = new();
            _transferService = new();

            // Initialize and wire events
            try
            {
                _wifiService.InitializeAsync().Wait();
                if (!_wifiService.IsHardwareSupported)
                {
                    _wifiService.Stop();
                    MessageText = "Hardware Wi-Fi Direct non supporté";
                }
                else
                {
                    _wifiService.StatusChanged += WifiService_StatusChanged;
                    _wifiService.PeerDiscovered += WifiService_PeerDiscovered;
                    _wifiService.PeerUpdated += WifiService_PeerUpdated;
                    _wifiService.PeerRemoved += WifiService_PeerRemoved;
                    _wifiService.InvitationReceived += WifiService_InvitationReceived;
                    _wifiService.ConnectionEstablished += WifiService_ConnectionEstablished;
                    _wifiService.ConnectionClosed += WifiService_ConnectionClosed;

                    _transferService.IncomingTransferRequested += OnIncomingTransferRequested;

                    _wifiService.StartPublishingAsync();
                    _wifiService.StartSubscribingAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                MessageText = "Initialisation impossible : " + ex.Message;
            }

            // Commands
            BrowseCommand = new RelayCommand(async _ => await ExecuteBrowseAsync());
            RetryCommand = new RelayCommand(_ => RetryPage());
            ReloadCommand = new RelayCommand(_ => Reload());
            DeleteAllCommand = new RelayCommand(_ => DeleteAll());
            DeleteItemCommand = new RelayCommand(param => DeleteItem(param as FileI), param => param is FileI);
            ConnectAndSendCommand = new RelayCommand(async param => await ConnectAndSendAsync(param as WifiDirectPeer), param => param is WifiDirectPeer);

            PendingFiles.CollectionChanged += (_, __) => { OnPropertyChanged(nameof(HasPendingFiles)); OnPropertyChanged(nameof(NoPendingFiles)); UpdateDropStatus(); };
        }

        // ---- ICommand implementations delegate helpers ----
        private async Task ExecuteBrowseAsync()
        {
            // Because ViewModels shouldn't show dialogs directly, use a simple OpenFileDialog as a pragmatic choice here
            // This keeps parity with previous behaviour; for a stricter MVVM approach an IDialogService should be injected.
            var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Multiselect = true, Title = "Select File(s) or Folder(s)", DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
            if (dialog.ShowDialog() == true)
            {
                AddToSender(dialog.FileNames);
            }
            await Task.CompletedTask;
        }

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

        private void Reload()
        {
            _ctsWatcher?.Cancel();
            DiscoveredDevices.Clear();
            _ctsWatcher = new();
            _wifiService.StartSubscribingAsync(_ctsWatcher.Token);
        }

        private void DeleteAll()
        {
            PendingFiles.Clear();
            UpdateDropStatus();
            Reload();
        }

        private void DeleteItem(FileI? file)
        {
            if (file == null) return;
            PendingFiles.Remove(file);
            UpdateDropStatus();
        }

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
                ProgressText = "Demande de connection en cours...";
                _ctsInvitation = new();
                _ctsConction = new();
                if (target.TestMode) { ProgressText = "Test reçu avec success "; return; }
                // Subscribe to connection established event and request connection
                await WaitConnectionEstablishAsync(target);
                await _wifiService.ConnectToPeerAsync(target, _ctsConction.Token);
            }
            catch (Exception ex)
            {
                ProgressText = "Erreur : " + ex.Message;
            }
        }

        // ---- Helper and event wiring methods ----
        private async Task WaitConnectionEstablishAsync(WifiDirectPeer targetDevice)
        {
            // Save/flag logic can be implemented if needed; previously used an event Sended — for simplicity, rely on existing ConnectionEstablished handler
            await Task.CompletedTask;
        }

        private void AddToSender(string[] files)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                PendingFiles.Clear();
                var r = from f in files where !string.IsNullOrWhiteSpace(f?.Trim()) && File.Exists(f) || Directory.Exists(f) select new FileI(f);
                foreach (var t in r)
                {
                    PendingFiles.Add(t);
                }
                UpdateDropStatus();
                if (_ctsWatcher == null || _ctsWatcher.IsCancellationRequested)
                {
                    Reload();
                }
            });
        }

        private void UpdateDropStatus()
        {
            DropStatusText = PendingFiles.Count <= 0 ? "Aucun élément sélectionné" : $"{PendingFiles.Count} élément(s) prêt(s). Sélectionnez un appareil en bas.";
        }

        private void WifiService_StatusChanged(object? sender, WifiDirectStatusEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() => MessageText = e.Status.ToString());
        }

        private void WifiService_PeerDiscovered(object? sender, WifiDirectPeerEventArgs e)
            => Application.Current.Dispatcher.Invoke(() => { if (DiscoveredDevices.Contains(e.Peer)) return; DiscoveredDevices.Add(e.Peer); });

        private void WifiService_PeerUpdated(object? sender, WifiDirectPeerEventArgs e)
            => Application.Current.Dispatcher.Invoke(() => RefreshPeer(e.Peer));

        private void WifiService_PeerRemoved(object? sender, WifiDirectPeerEventArgs e)
            => Application.Current.Dispatcher.Invoke(() => RemovePeer(e.Peer));

        private async void WifiService_InvitationReceived(object? sender, WifiDirectInvitationEventArgs e)
        {
            await Application.Current.Dispatcher.InvokeAsync(async () => await HandleInvitationAsync(e));
        }

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

        private void WifiService_ConnectionEstablished(object? sender, WifiDirectConnectionEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(async () =>
            {
                // Configure transfer endpoint
                var localIp = e.LocalIpAddress;
                var localPort = 5050;
                _ = _transferService.StartListeningAsync(localIp, localPort, CancellationToken.None);
            });
        }

        private void WifiService_ConnectionClosed(object? sender, WifiDirectConnectionEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // clear any runtime IPs if desired
            });
        }

        private void OnIncomingTransferRequested(object? sender, TransferSessionMetadata metadata)
        {
            Application.Current.Dispatcher.Invoke(async () =>
            {
                var msg = $"{metadata.SenderName} veut vous envoyer {metadata.TotalFiles} fichiers ({(metadata.TotalSize / 1024 / 1024)} Mo).\nAccepter ?";
                if (MessageBox.Show(msg, "Réception", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    _ctsTransfert = new();
                    string destPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Wi-Fi P2P");
                    Directory.CreateDirectory(destPath);

                    var progress = new Progress<FileTransferProgress>(p =>
                    {
                        TransferProgress = p.Percentage;
                        ProgressText = $"Réception : {p.CurrentFileName} ({p.Percentage:0.0}%)";
                    });

                    try
                    {
                        await _transferService.DownloadSessionAsync(metadata, destPath, progress, _ctsTransfert.Token);
                        ProgressText = "Transfert terminé avec succès !";
                    }
                    catch (Exception ex)
                    {
                        ProgressText = "Erreur de téléchargement : " + ex.Message;
                    }
                }
            });
        }

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
            });
        }

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

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Minimal RelayCommand implementation
        private class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Predicate<object?>? _canExecute;

            public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);
            public event EventHandler? CanExecuteChanged;
            public void Execute(object? parameter) => _execute(parameter);
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
