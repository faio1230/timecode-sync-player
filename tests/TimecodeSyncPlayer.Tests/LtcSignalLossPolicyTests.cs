using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class LtcSignalLossPolicyTests
{
    private static readonly DateTime Start = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_ReceivingToLost_ReturnsPauseOnceAtTimeoutEdge()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);

        policy.Evaluate(Start.AddMilliseconds(249), context).Should().Be(LtcSignalLossAction.None);
        policy.Evaluate(Start.AddMilliseconds(250), context).Should().Be(LtcSignalLossAction.Pause);
        policy.Evaluate(Start.AddSeconds(1), context with { IsPlaybackPaused = true })
            .Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AfterOperatorManuallyPlays_DoesNotPauseAgainDuringSameLoss()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);
        policy.Evaluate(Start.AddMilliseconds(250), context).Should().Be(LtcSignalLossAction.Pause);

        policy.Evaluate(Start.AddMilliseconds(350), context with { IsPlaybackPaused = false })
            .Should().Be(LtcSignalLossAction.None);
        policy.Evaluate(Start.AddSeconds(2), context with { IsPlaybackPaused = false })
            .Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();

        for (int frame = 0; frame < 5; frame++)
        {
            policy.ObserveValidFrame(
                    Start.AddSeconds(3).AddMilliseconds(frame * 40),
                    context with { IsPlaybackPaused = false })
                .Should().Be(LtcSignalLossAction.None);
        }
    }

    [Fact]
    public void ObserveValidFrame_AfterRequiredConsecutiveFrames_ReturnsResumeAndSync()
    {
        var policy = CreatePolicy(resumeFrames: 5);
        LtcSignalLossContext receiving = Context();
        LtcSignalLossContext paused = receiving with { IsPlaybackPaused = true };
        policy.ObserveValidFrame(Start, receiving);
        policy.Evaluate(Start.AddMilliseconds(250), receiving).Should().Be(LtcSignalLossAction.Pause);

        for (int frame = 1; frame < 5; frame++)
        {
            policy.ObserveValidFrame(Start.AddMilliseconds(300 + frame * 40), paused)
                .Should().Be(LtcSignalLossAction.None);
            policy.ShouldSuppressSync.Should().BeTrue();
        }

        policy.ObserveValidFrame(Start.AddMilliseconds(500), paused)
            .Should().Be(LtcSignalLossAction.ResumeAndSync);
        policy.ShouldSuppressSync.Should().BeFalse();
    }

    [Fact]
    public void ObserveValidFrame_FewerThanRequiredFrames_DoesNotResume()
    {
        var policy = CreatePolicy(resumeFrames: 5);
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);
        policy.Evaluate(Start.AddMilliseconds(250), context).Should().Be(LtcSignalLossAction.Pause);

        for (int frame = 0; frame < 4; frame++)
        {
            policy.ObserveValidFrame(
                    Start.AddMilliseconds(300 + frame * 40),
                    context with { IsPlaybackPaused = true })
                .Should().Be(LtcSignalLossAction.None);
        }

        policy.ShouldSuppressSync.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_GapBetweenRecoveryFrames_ResetsConsecutiveCount()
    {
        var policy = CreatePolicy(resumeFrames: 2);
        LtcSignalLossContext context = Context();
        LtcSignalLossContext paused = context with { IsPlaybackPaused = true };
        policy.ObserveValidFrame(Start, context);
        policy.Evaluate(Start.AddMilliseconds(250), context).Should().Be(LtcSignalLossAction.Pause);
        policy.ObserveValidFrame(Start.AddMilliseconds(300), paused).Should().Be(LtcSignalLossAction.None);

        policy.Evaluate(Start.AddMilliseconds(550), paused).Should().Be(LtcSignalLossAction.None);
        policy.ObserveValidFrame(Start.AddMilliseconds(600), paused).Should().Be(LtcSignalLossAction.None);
        policy.ObserveValidFrame(Start.AddMilliseconds(640), paused)
            .Should().Be(LtcSignalLossAction.ResumeAndSync);
    }

    [Theory]
    [InlineData(LtcSignalLossMode.RunThrough, true, true, false)]
    [InlineData(LtcSignalLossMode.Stop, false, true, false)]
    [InlineData(LtcSignalLossMode.Stop, true, false, false)]
    [InlineData(LtcSignalLossMode.Stop, true, true, true)]
    public void Evaluate_WhenActivationConditionIsNotMet_ReturnsNone(
        LtcSignalLossMode mode,
        bool syncEnabled,
        bool isMonitoring,
        bool isGapActive)
    {
        var policy = CreatePolicy();
        var context = new LtcSignalLossContext(
            mode,
            syncEnabled,
            isMonitoring,
            isGapActive,
            IsPlaybackPaused: false);
        policy.ObserveValidFrame(Start, context with { IsMonitoring = true });

        policy.Evaluate(Start.AddMilliseconds(250), context).Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenPlaybackWasAlreadyPaused_DoesNotOwnPauseOrResume()
    {
        var policy = CreatePolicy(resumeFrames: 1);
        LtcSignalLossContext paused = Context() with { IsPlaybackPaused = true };
        policy.ObserveValidFrame(Start, paused);

        policy.Evaluate(Start.AddMilliseconds(250), paused).Should().Be(LtcSignalLossAction.None);
        policy.ObserveValidFrame(Start.AddMilliseconds(300), paused)
            .Should().Be(LtcSignalLossAction.None);
    }

    [Fact]
    public void RunThroughMode_LossAndRecoveryNeverSuppressesExistingSyncFlow()
    {
        var policy = CreatePolicy(resumeFrames: 2);
        LtcSignalLossContext context = Context() with { Mode = LtcSignalLossMode.RunThrough };
        policy.ObserveValidFrame(Start, context);

        policy.Evaluate(Start.AddMilliseconds(250), context).Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();
        policy.ObserveValidFrame(Start.AddMilliseconds(300), context)
            .Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();
        policy.ObserveValidFrame(Start.AddMilliseconds(340), context)
            .Should().Be(LtcSignalLossAction.None);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ObserveValidFrame_WhenRecoveryConditionIsNotMet_DoesNotResume(
        bool syncEnabled,
        bool isMonitoring,
        bool isGapActive)
    {
        var policy = CreatePolicy(resumeFrames: 1);
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);
        policy.Evaluate(Start.AddMilliseconds(250), context).Should().Be(LtcSignalLossAction.Pause);

        var recoveryContext = context with
        {
            SyncEnabled = syncEnabled,
            IsMonitoring = isMonitoring,
            IsGapActive = isGapActive,
            IsPlaybackPaused = true,
        };

        policy.ObserveValidFrame(Start.AddMilliseconds(300), recoveryContext)
            .Should().Be(LtcSignalLossAction.None);
    }

    [Fact]
    public void Reset_RequiresANewFrameBeforeAnotherLossCanBeDetected()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);
        policy.Evaluate(Start.AddMilliseconds(250), context).Should().Be(LtcSignalLossAction.Pause);

        policy.Reset();

        policy.Evaluate(Start.AddSeconds(5), context).Should().Be(LtcSignalLossAction.None);
    }

    [Fact]
    public void Constructor_WithInvalidThresholdThrows()
    {
        Action invalidTimeout = () => new LtcSignalLossPolicy(TimeSpan.Zero, 5);
        Action invalidFrames = () => new LtcSignalLossPolicy(TimeSpan.FromMilliseconds(250), 0);

        invalidTimeout.Should().Throw<ArgumentOutOfRangeException>();
        invalidFrames.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static LtcSignalLossPolicy CreatePolicy(int resumeFrames = 5) =>
        new(TimeSpan.FromMilliseconds(250), resumeFrames);

    private static LtcSignalLossContext Context() => new(
        LtcSignalLossMode.Stop,
        SyncEnabled: true,
        IsMonitoring: true,
        IsGapActive: false,
        IsPlaybackPaused: false);
}
