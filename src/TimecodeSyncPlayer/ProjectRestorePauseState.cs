namespace TimecodeSyncPlayer;

/// <summary>
/// プロジェクト復元が設定した自動 pause の所有権を追跡する。
/// オペレーター操作や通常のメディアロードとは独立して扱う。
/// </summary>
internal sealed class ProjectRestorePauseState
{
    public bool IsPending { get; private set; }

    public void MarkPending() => IsPending = true;

    public void Clear() => IsPending = false;

    public bool TryConsume()
    {
        if (!IsPending)
            return false;

        IsPending = false;
        return true;
    }
}
