namespace TimecodeSyncPlayer;

public enum ContinueSyncSeekSkipReason
{
    None,
    NoSeekDecision,
    Suppressed,
    Debounced,
    // D37-a: 粗い判定のゲートが Seek を保留している（要求は Deferred のまま維持する）。
    GateDeferred
}

public sealed record ContinueSyncSeekPlan(
    bool ShouldSeek,
    double TargetSeconds,
    ContinueSyncSeekSkipReason SkipReason)
{
    public static ContinueSyncSeekPlan Skip(ContinueSyncSeekSkipReason reason) => new(false, 0.0, reason);
}

public static class ContinueSyncSeekPlanner
{
    public static ContinueSyncSeekPlan Decide(SyncDecision decision, bool suppressSeek, bool isDebounced)
    {
        if (decision.GateDeferred)
            return ContinueSyncSeekPlan.Skip(ContinueSyncSeekSkipReason.GateDeferred);

        if (decision.Action != SyncActionType.Seek)
            return ContinueSyncSeekPlan.Skip(ContinueSyncSeekSkipReason.NoSeekDecision);

        if (suppressSeek)
            return ContinueSyncSeekPlan.Skip(ContinueSyncSeekSkipReason.Suppressed);

        if (isDebounced)
            return ContinueSyncSeekPlan.Skip(ContinueSyncSeekSkipReason.Debounced);

        return new ContinueSyncSeekPlan(true, decision.TargetSeconds, ContinueSyncSeekSkipReason.None);
    }
}
