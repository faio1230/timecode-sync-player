namespace TimecodeSyncPlayer;

[Flags]
internal enum PauseOwners
{
    None = 0,
    SignalLoss = 1,
    BoundaryHold = 2,
    Gap = 4,
    ProjectRestore = 8,
    // v0.5.4 K5（§6 の 15）: 利用者が自分の操作で止めている（再生ボタンで解除するまで）。
    User = 16,
}
