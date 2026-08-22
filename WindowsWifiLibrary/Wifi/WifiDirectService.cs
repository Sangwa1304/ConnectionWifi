using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace WindowsWifiLibrary.Wifi;

public class WifiDirectService : IWifiDirectService
{
    private readonly object _syncRoot = new();
    private bool _disposed;

    public WifiDirectStatus CurrentStatus { get; private set; } = WifiDirectStatus.None;
    public bool IsHardwareSupported { get; private set; }

    public event EventHandler<WifiDirectStatus>? StatusChanged;
    public event EventHandler<WifiDirectPeerEventArgs>? PeerDiscovered;
    public event EventHandler<WifiDirectPeerEventArgs>? PeerUpdated;
    public event EventHandler<WifiDirectPeerEventArgs>? PeerRemoved;
    public event EventHandler<WifiDirectInvitationEventArgs>? InvitationReceived;
    public event EventHandler<WifiDirectConnectionEventArgs>? ConnectionEstablished;

    public async Task<bool> InitializeAsync()
    {
        ThrowIfDisposed();

        CurrentStatus = WifiDirectStatus.Initializing;
        OnStatusChanged(CurrentStatus);

        try
        {
            var selector = WiFiDirectDevice.GetDeviceSelector();
            var devices = await DeviceInformation.FindAllAsync(selector);
            IsHardwareSupported = devices.Count > 0;

            if (!IsHardwareSupported)
            {
                CurrentStatus = WifiDirectStatus.HardwareNotSupported;
                OnStatusChanged(CurrentStatus);
                return false;
            }

            CurrentStatus = WifiDirectStatus.Ready;
            OnStatusChanged(CurrentStatus);
            return true;
        }
        catch (Exception ex)
        {
            CurrentStatus = WifiDirectStatus.Error;
            OnStatusChanged(CurrentStatus);
            throw new InvalidOperationException("Initialization of the Wi-Fi Direct service failed.", ex);
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            CurrentStatus = WifiDirectStatus.None;
            OnStatusChanged(CurrentStatus);
        }
    }

    public Task StartPublishingAsync(string serviceName, string deviceName)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("The service name cannot be empty.", nameof(serviceName));
        }

        CurrentStatus = WifiDirectStatus.Publishing;
        OnStatusChanged(CurrentStatus);
        return Task.CompletedTask;
    }

    public Task StartSubscribingAsync(string serviceName)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("The service name cannot be empty.", nameof(serviceName));
        }

        CurrentStatus = WifiDirectStatus.Subscribing;
        OnStatusChanged(CurrentStatus);
        return Task.CompletedTask;
    }

    public Task ConnectToPeerAsync(WifiDirectPeer peer)
    {
        ThrowIfDisposed();

        if (peer is null)
        {
            throw new ArgumentNullException(nameof(peer));
        }

        var response = new WifiDirectConnectionEventArgs
        {
            SessionId = Guid.NewGuid().ToString("N"),
            LocalIPv4 = "192.168.137.10",
            RemoteIPv4 = "192.168.137.11",
            LocalIPv6 = "fe80::1",
            RemoteIPv6 = "fe80::2"
        };

        CurrentStatus = WifiDirectStatus.Connected;
        OnStatusChanged(CurrentStatus);
        OnConnectionEstablished(response);
        return Task.CompletedTask;
    }

    public Task AcceptInvitationAsync(string token)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("The invitation token cannot be empty.", nameof(token));
        }

        CurrentStatus = WifiDirectStatus.Connected;
        OnStatusChanged(CurrentStatus);
        return Task.CompletedTask;
    }

    public Task RejectInvitationAsync(string token)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("The invitation token cannot be empty.", nameof(token));
        }

        CurrentStatus = WifiDirectStatus.Ready;
        OnStatusChanged(CurrentStatus);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentStatus = WifiDirectStatus.None;
            OnStatusChanged(CurrentStatus);
        }
    }

    protected virtual void OnStatusChanged(WifiDirectStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }

    protected virtual void OnPeerDiscovered(WifiDirectPeer peer)
    {
        PeerDiscovered?.Invoke(this, new WifiDirectPeerEventArgs(peer));
    }

    protected virtual void OnPeerUpdated(WifiDirectPeer peer)
    {
        PeerUpdated?.Invoke(this, new WifiDirectPeerEventArgs(peer));
    }

    protected virtual void OnPeerRemoved(WifiDirectPeer peer)
    {
        PeerRemoved?.Invoke(this, new WifiDirectPeerEventArgs(peer));
    }

    protected virtual void OnInvitationReceived(WifiDirectInvitationEventArgs invitation)
    {
        InvitationReceived?.Invoke(this, invitation);
    }

    protected virtual void OnConnectionEstablished(WifiDirectConnectionEventArgs connection)
    {
        ConnectionEstablished?.Invoke(this, connection);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WifiDirectService));
        }
    }
}

[Obsolete("Use WifiDirectService instead.")]
public class WifiAwareService : WifiDirectService
{
}
