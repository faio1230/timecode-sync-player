namespace TimecodeSyncPlayer;

[Flags]
internal enum PauseOwners
{
    None = 0,
    SignalLoss = 1,
    BoundaryHold = 2,
    Gap = 4,
    ProjectRestore = 8,
}
