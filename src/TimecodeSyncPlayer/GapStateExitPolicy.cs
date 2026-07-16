namespace TimecodeSyncPlayer;

/// <summary>
/// ギャップ演出状態（Freeze/Black）の手動解除判定。
/// ギャップ状態は「Sync ON かつ Continue モード」でのみ維持できる。
/// Single へ切替、または Sync OFF にした時点で解除し、通常レンダリングへ戻す。
/// </summary>
internal static class GapStateExitPolicy
{
    public static bool ShouldExit(bool syncEnabled, SyncMode syncMode, bool gapStateActive)
        => gapStateActive && (!syncEnabled || syncMode == SyncMode.Single);
}
