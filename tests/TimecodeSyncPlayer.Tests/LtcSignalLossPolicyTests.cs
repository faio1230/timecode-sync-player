using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class LtcSignalLossPolicyTests
{
    private const long Start = 10_000;

    [Fact]
    public void Evaluate_ReceivingToLost_ReturnsPauseOnceAtTimeoutEdge()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);

        policy.Evaluate(At(249), context).Should().Be(LtcSignalLossAction.None);
        policy.Evaluate(At(250), context).Should().Be(LtcSignalLossAction.Pause);
        policy.Evaluate(At(1000), context with { IsPlaybackPaused = true })
            .Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AfterOperatorManuallyPlays_DoesNotPauseAgainDuringSameLoss()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);
        policy.Evaluate(At(250), context).Should().Be(LtcSignalLossAction.Pause);

        policy.Evaluate(At(350), context with { IsPlaybackPaused = false })
            .Should().Be(LtcSignalLossAction.None);
        policy.Evaluate(At(2000), context with { IsPlaybackPaused = false })
            .Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();

        for (int frame = 0; frame < 5; frame++)
        {
            policy.ObserveValidFrame(
                    At(3000 + frame * 40),
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
        policy.Evaluate(At(250), receiving).Should().Be(LtcSignalLossAction.Pause);

        for (int frame = 1; frame < 5; frame++)
        {
            policy.ObserveValidFrame(At(300 + frame * 40), paused)
                .Should().Be(LtcSignalLossAction.None);
            policy.ShouldSuppressSync.Should().BeTrue();
        }

        policy.ObserveValidFrame(At(500), paused)
            .Should().Be(LtcSignalLossAction.ResumeAndSync);
        policy.ShouldSuppressSync.Should().BeFalse();
    }

    [Fact]
    public void ObserveValidFrame_FewerThanRequiredFrames_DoesNotResume()
    {
        var policy = CreatePolicy(resumeFrames: 5);
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);
        policy.Evaluate(At(250), context).Should().Be(LtcSignalLossAction.Pause);

        for (int frame = 0; frame < 4; frame++)
        {
            policy.ObserveValidFrame(
                    At(300 + frame * 40),
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
        policy.Evaluate(At(250), context).Should().Be(LtcSignalLossAction.Pause);
        policy.ObserveValidFrame(At(300), paused).Should().Be(LtcSignalLossAction.None);

        policy.Evaluate(At(550), paused).Should().Be(LtcSignalLossAction.None);
        policy.ObserveValidFrame(At(600), paused).Should().Be(LtcSignalLossAction.None);
        policy.ObserveValidFrame(At(640), paused)
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

        policy.Evaluate(At(250), context).Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenPlaybackWasAlreadyPaused_DoesNotOwnPauseOrResume()
    {
        var policy = CreatePolicy(resumeFrames: 1);
        LtcSignalLossContext paused = Context() with { IsPlaybackPaused = true };
        policy.ObserveValidFrame(Start, paused);

        policy.Evaluate(At(250), paused).Should().Be(LtcSignalLossAction.None);
        policy.ObserveValidFrame(At(300), paused)
            .Should().Be(LtcSignalLossAction.None);
    }

    [Fact]
    public void Evaluate_WhenAlreadyPausedAtLossThenManuallyPlayed_DoesNotPauseOnNextTick()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext paused = Context() with { IsPlaybackPaused = true };
        policy.ObserveValidFrame(Start, paused);
        policy.Evaluate(At(250), paused).Should().Be(LtcSignalLossAction.None);

        LtcSignalLossContext manuallyPlaying = paused with { IsPlaybackPaused = false };
        policy.Evaluate(At(350), manuallyPlaying).Should().Be(LtcSignalLossAction.None);
        policy.Evaluate(At(450), manuallyPlaying).Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WhenLoadFileUnpausesDuringLoss_DoesNotImmediatelyPauseAgain()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext loadingPaused = Context() with
        {
            Mode = LtcSignalLossMode.RunThrough,
            IsPlaybackPaused = true,
        };
        policy.ObserveValidFrame(Start, loadingPaused);
        policy.Evaluate(At(250), loadingPaused).Should().Be(LtcSignalLossAction.None);

        LtcSignalLossContext loadCompleted = loadingPaused with
        {
            Mode = LtcSignalLossMode.Stop,
            IsPlaybackPaused = false,
        };
        policy.Evaluate(At(350), loadCompleted).Should().Be(LtcSignalLossAction.None);
        policy.Evaluate(At(450), loadCompleted).Should().Be(LtcSignalLossAction.None);
    }

    [Fact]
    public void RunThroughMode_LossAndRecoveryNeverSuppressesExistingSyncFlow()
    {
        var policy = CreatePolicy(resumeFrames: 2);
        LtcSignalLossContext context = Context() with { Mode = LtcSignalLossMode.RunThrough };
        policy.ObserveValidFrame(Start, context);

        policy.Evaluate(At(250), context).Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();
        policy.ObserveValidFrame(At(300), context)
            .Should().Be(LtcSignalLossAction.None);
        policy.ShouldSuppressSync.Should().BeFalse();
        policy.ObserveValidFrame(At(340), context)
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
        policy.Evaluate(At(250), context).Should().Be(LtcSignalLossAction.Pause);

        var recoveryContext = context with
        {
            SyncEnabled = syncEnabled,
            IsMonitoring = isMonitoring,
            IsGapActive = isGapActive,
            IsPlaybackPaused = true,
        };

        policy.ObserveValidFrame(At(300), recoveryContext)
            .Should().Be(LtcSignalLossAction.None);
    }

    [Fact]
    public void Evaluate_AfterLossInRunThroughMode_PausesWhenModeChangesToStop()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext runThrough = Context() with { Mode = LtcSignalLossMode.RunThrough };
        policy.ObserveValidFrame(Start, runThrough);
        policy.Evaluate(At(250), runThrough).Should().Be(LtcSignalLossAction.None);

        policy.Evaluate(At(350), runThrough with { Mode = LtcSignalLossMode.Stop })
            .Should().Be(LtcSignalLossAction.Pause);
        policy.Evaluate(
                At(450),
                runThrough with { Mode = LtcSignalLossMode.Stop, IsPlaybackPaused = true })
            .Should().Be(LtcSignalLossAction.None);
    }

    [Fact]
    public void Evaluate_AfterLossWhileSyncOff_PausesWhenSyncTurnsOn()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext syncOff = Context() with { SyncEnabled = false };
        policy.ObserveValidFrame(Start, syncOff);
        policy.Evaluate(At(250), syncOff).Should().Be(LtcSignalLossAction.None);

        policy.Evaluate(At(350), syncOff with { SyncEnabled = true })
            .Should().Be(LtcSignalLossAction.Pause);
    }

    [Fact]
    public void Evaluate_AfterUnexpectedDeviceStop_TreatsMissingFramesAsSignalLoss()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext receiving = Context();
        policy.ObserveValidFrame(Start, receiving);

        // 異常停止後も、呼び出し側が信号断検出を有効として扱う。
        policy.Evaluate(At(250), receiving with { IsMonitoring = true })
            .Should().Be(LtcSignalLossAction.Pause);
    }

    [Fact]
    public void Evaluate_AfterManualPlay_DoesNotPauseWhenModeIsToggledDuringSameLoss()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext stop = Context();
        policy.ObserveValidFrame(Start, stop);
        policy.Evaluate(At(250), stop).Should().Be(LtcSignalLossAction.Pause);
        policy.Evaluate(At(350), stop with { IsPlaybackPaused = false })
            .Should().Be(LtcSignalLossAction.None);

        policy.Evaluate(At(450), stop with { Mode = LtcSignalLossMode.RunThrough })
            .Should().Be(LtcSignalLossAction.None);
        policy.Evaluate(At(550), stop).Should().Be(LtcSignalLossAction.None);
    }

    [Fact]
    public void Reset_RequiresANewFrameBeforeAnotherLossCanBeDetected()
    {
        var policy = CreatePolicy();
        LtcSignalLossContext context = Context();
        policy.ObserveValidFrame(Start, context);
        policy.Evaluate(At(250), context).Should().Be(LtcSignalLossAction.Pause);

        policy.Reset();

        policy.Evaluate(At(5000), context).Should().Be(LtcSignalLossAction.None);
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

    private static long At(int elapsedMilliseconds) => Start + elapsedMilliseconds;

    private static LtcSignalLossContext Context() => new(
        LtcSignalLossMode.Stop,
        SyncEnabled: true,
        IsMonitoring: true,
        IsGapActive: false,
        IsPlaybackPaused: false);
}
