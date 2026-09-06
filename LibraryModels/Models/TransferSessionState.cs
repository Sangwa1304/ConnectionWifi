namespace LibraryMI.Models;

public enum TransferSessionState
{
    Created = 0,
    Negotiating = 1,
    ReadyToSend = 2,
    Sending = 3,
    Receiving = 4,
    Completed = 5,
    Cancelled = 6,
    Failed = 7
}
