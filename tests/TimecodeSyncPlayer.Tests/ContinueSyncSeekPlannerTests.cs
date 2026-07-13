using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ContinueSyncSeekPlannerTests
{
    [Fact]
    public void Decide_ReturnsSeek_WhenDecisionRequestsSeekAndNotSuppressedOrDebounced()
    {
        SyncDecision decision = CreateSeekDecision(targetSeconds: 12.3);

        ContinueSyncSeekPlan plan = ContinueSyncSeekPlanner.Decide(decision, suppressSeek: false, isDebounced: false);

        plan.ShouldSeek.Should().BeTrue();
        plan.TargetSeconds.Should().Be(12.3);
        plan.SkipReason.Should().Be(ContinueSyncSeekSkipReason.None);
    }

    [Fact]
    public void Decide_Skips_WhenDecisionDoesNotRequestSeek()
    {
        ContinueSyncSeekPlan plan = ContinueSyncSeekPlanner.Decide(SyncDecision.None, suppressSeek: false, isDebounced: false);

        plan.ShouldSeek.Should().BeFalse();
        plan.SkipReason.Should().Be(ContinueSyncSeekSkipReason.NoSeekDecision);
    }

    [Fact]
    public void Decide_Skips_WhenSeekIsSuppressed()
    {
        SyncDecision decision = CreateSeekDecision(targetSeconds: 12.3);

        ContinueSyncSeekPlan plan = ContinueSyncSeekPlanner.Decide(decision, suppressSeek: true, isDebounced: false);

        plan.ShouldSeek.Should().BeFalse();
        plan.SkipReason.Should().Be(ContinueSyncSeekSkipReason.Suppressed);
    }

    [Fact]
    public void Decide_Skips_WhenSeekIsDebounced()
    {
        SyncDecision decision = CreateSeekDecision(targetSeconds: 12.3);

        ContinueSyncSeekPlan plan = ContinueSyncSeekPlanner.Decide(decision, suppressSeek: false, isDebounced: true);

        plan.ShouldSeek.Should().BeFalse();
        plan.SkipReason.Should().Be(ContinueSyncSeekSkipReason.Debounced);
    }

    private static SyncDecision CreateSeekDecision(double targetSeconds) => new(
        SyncActionType.Seek,
        targetSeconds,
        DeltaSeconds: 0.25,
        ToleranceSeconds: 0.04,
        VideoFpsUsed: 29.97,
        TimecodeFpsUsed: 29.97,
        UsedDefaultVideoFps: false,
        UsedDefaultTimecodeFps: false);
}
