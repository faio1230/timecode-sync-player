namespace TimecodeSyncPlayer;

/// <summary>Whether the accepted request was handled or is waiting on transient player state.</summary>
internal enum SyncRequestResult
{
    Complete,
    Deferred
}
