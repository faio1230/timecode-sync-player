namespace TimecodeSyncPlayer.Output;

/// <summary>
/// D-2: 即時 Present の待機で latency waitable が未シグナルだった場合に、同じ合成 tick で
/// notReady を何度も記録しない（1 tick 最大 1 回）。時刻は VblankDisplayGate.TimeoutUntilMs で
/// vblank−lead まで待ってから判定する。
/// </summary>
internal sealed class VblankNotReadyGate
{
    private long lastSlot = long.MinValue;

    public bool ShouldRecord(long slot)
    {
        if (slot == lastSlot) return false;
        lastSlot = slot;
        return true;
    }
}
