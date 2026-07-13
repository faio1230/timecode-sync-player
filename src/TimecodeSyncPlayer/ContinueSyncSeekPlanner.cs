namespace TimecodeSyncPlayer;

public enum ContinueSyncSeekSkipReason
{
    None,
    NoSeekDecision,
    Suppressed,
    Debounced
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
        if (decision.Action != SyncActionType.Seek)
            return ContinueSyncSeekPlan.Skip(ContinueSyncSeekSkipReason.NoSeekDecision);

        if (suppressSeek)
            return ContinueSyncSeekPlan.Skip(ContinueSyncSeekSkipReason.Suppressed);

        if (isDebounced)
            return ContinueSyncSeekPlan.Skip(ContinueSyncSeekSkipReason.Debounced);

        return new ContinueSyncSeekPlan(true, decision.TargetSeconds, ContinueSyncSeekSkipReason.None);
    }
}
