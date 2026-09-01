namespace WindowsWifiWpf.ViewModels;

public enum TransferWorkflowState
{
    Idle,
    DiscoveringPeers,
    PeerSelected,
    PairingRequested,
    TransferNegotiation,
    Sending,
    Receiving,
    Completed,
    Cancelled,
    Error
}
