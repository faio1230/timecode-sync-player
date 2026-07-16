using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class LtcFrameProcessorTests
{
    [Fact]
    public void Process_UsesFixedFpsForResolvedSecondsAndStatusText()
    {
        var processor = new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics());
        var frame = Frame(new LtcTimecode(0, 0, 10, 12, DropFrame: false), detectedFps: 30.0, rawSeconds: 99.0);

        LtcFrameProcessingResult result = processor.Process(frame, TimecodeFpsMode.Fixed25);

        result.TimecodeText.Should().Be("00:00:10:12");
        result.RealTimeText.Should().Be("99.000 s");
        result.ResolvedSeconds.Should().BeApproximately(10.48, 0.0001);
        result.ResolvedFps.Should().Be(25.0);
        result.FormatText.Should().Be("Fixed: 25 fps  detect: 30 fps");
        result.Diagnostic.Status.Should().Be(TimecodeFrameDiagnosticStatus.Initial);
        result.ShouldApplySync.Should().BeTrue();
        result.ShouldLogFps.Should().BeTrue();
    }

    [Fact]
    public void Process_UsesRawSecondsUntilAutoFpsLocks()
    {
        var processor = new LtcFrameProcessor(new TimecodeFpsSelector(confirmCount: 3, changeCount: 8), new TimecodeFrameDiagnostics());
        var frame = Frame(new LtcTimecode(0, 0, 10, 12, DropFrame: false), detectedFps: 30.0, rawSeconds: 99.0);

        LtcFrameProcessingResult result = processor.Process(frame, TimecodeFpsMode.Auto);

        result.ResolvedFps.Should().Be(0.0);
        result.ResolvedSeconds.Should().Be(99.0);
        result.FormatText.Should().Be("Auto: 検出中  detect: 30 fps");
        result.Diagnostic.Status.Should().Be(TimecodeFrameDiagnosticStatus.Initial);
        result.ShouldApplySync.Should().BeTrue();
    }

    [Fact]
    public void Process_SkipsSyncForSingleJumpFrame()
    {
        var processor = new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics());

        processor.Process(Frame(new LtcTimecode(0, 0, 10, 0, DropFrame: false), 30.0, 10.0), TimecodeFpsMode.Fixed30);
        LtcFrameProcessingResult result = processor.Process(
            Frame(new LtcTimecode(0, 1, 10, 0, DropFrame: false), 30.0, 70.0),
            TimecodeFpsMode.Fixed30);

        result.Diagnostic.Status.Should().Be(TimecodeFrameDiagnosticStatus.Jump);
        result.ShouldApplySync.Should().BeFalse();
    }

    [Fact]
    public void Process_LogsFpsOnlyWhenResolvedFpsChanges()
    {
        var processor = new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics());

        processor.Process(Frame(new LtcTimecode(0, 0, 1, 0, DropFrame: false), 30.0, 1.0), TimecodeFpsMode.Fixed30)
            .ShouldLogFps.Should().BeTrue();
        processor.Process(Frame(new LtcTimecode(0, 0, 1, 1, DropFrame: false), 30.0, 1.033), TimecodeFpsMode.Fixed30)
            .ShouldLogFps.Should().BeFalse();
        processor.Process(Frame(new LtcTimecode(0, 0, 1, 2, DropFrame: false), 30.0, 1.066), TimecodeFpsMode.Fixed25)
            .ShouldLogFps.Should().BeTrue();
    }

    [Fact]
    public void ResetForFpsMode_SeedsFixedFpsAndResetsDiagnostics()
    {
        var processor = new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics());

        processor.Process(Frame(new LtcTimecode(0, 0, 10, 0, DropFrame: false), 30.0, 10.0), TimecodeFpsMode.Fixed30);
        processor.Process(Frame(new LtcTimecode(0, 0, 20, 0, DropFrame: false), 30.0, 20.0), TimecodeFpsMode.Fixed30);

        processor.ResetForFpsMode(TimecodeFpsMode.Fixed25);
        LtcFrameProcessingResult result = processor.Process(
            Frame(new LtcTimecode(0, 0, 10, 12, DropFrame: false), 30.0, 99.0),
            TimecodeFpsMode.Fixed25);

        result.ResolvedSeconds.Should().BeApproximately(10.48, 0.0001);
        result.Diagnostic.Status.Should().Be(TimecodeFrameDiagnosticStatus.Initial);
        result.ShouldLogFps.Should().BeTrue();
    }

    [Fact]
    public void ResetDiagnostics_MakesNextFrameInitialWithoutResettingFps()
    {
        var processor = new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics());

        processor.Process(Frame(new LtcTimecode(0, 0, 10, 0, DropFrame: false), 30.0, 10.0), TimecodeFpsMode.Fixed30);
        processor.Process(Frame(new LtcTimecode(0, 0, 20, 0, DropFrame: false), 30.0, 20.0), TimecodeFpsMode.Fixed30);

        processor.ResetDiagnostics();
        LtcFrameProcessingResult result = processor.Process(
            Frame(new LtcTimecode(0, 0, 10, 1, DropFrame: false), 30.0, 10.033),
            TimecodeFpsMode.Fixed30);

        result.ResolvedFps.Should().Be(30.0);
        result.Diagnostic.Status.Should().Be(TimecodeFrameDiagnosticStatus.Initial);
    }

    [Fact]
    public void Process_MidnightRolloverIsSingleJumpThenNormalWithoutSyncingJumpFrame()
    {
        var processor = new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics());

        processor.Process(
            Frame(new LtcTimecode(23, 59, 59, 24, DropFrame: false), 25.0, 86_399.96),
            TimecodeFpsMode.Fixed25);
        LtcFrameProcessingResult rollover = processor.Process(
            Frame(new LtcTimecode(0, 0, 0, 0, DropFrame: false), 25.0, 0.0),
            TimecodeFpsMode.Fixed25);
        LtcFrameProcessingResult next = processor.Process(
            Frame(new LtcTimecode(0, 0, 0, 1, DropFrame: false), 25.0, 0.04),
            TimecodeFpsMode.Fixed25);

        rollover.Diagnostic.Status.Should().Be(TimecodeFrameDiagnosticStatus.Jump);
        rollover.ShouldApplySync.Should().BeFalse();
        next.Diagnostic.Status.Should().Be(TimecodeFrameDiagnosticStatus.Normal);
        next.ShouldApplySync.Should().BeTrue();
    }

    [Fact]
    public void Process_ConsecutiveReverseFramesRemainReverseAndSuppressSync()
    {
        var processor = new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics());
        processor.Process(
            Frame(new LtcTimecode(0, 0, 10, 3, DropFrame: false), 25.0, 10.12),
            TimecodeFpsMode.Fixed25);

        LtcFrameProcessingResult[] results =
        [
            processor.Process(Frame(new LtcTimecode(0, 0, 10, 2, false), 25.0, 10.08), TimecodeFpsMode.Fixed25),
            processor.Process(Frame(new LtcTimecode(0, 0, 10, 1, false), 25.0, 10.04), TimecodeFpsMode.Fixed25),
            processor.Process(Frame(new LtcTimecode(0, 0, 10, 0, false), 25.0, 10.00), TimecodeFpsMode.Fixed25),
        ];

        results.Should().OnlyContain(result =>
            result.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Reverse &&
            !result.ShouldApplySync);
        results.Select(result => result.Diagnostic.DeltaFrames)
            .Should().OnlyContain(delta => Math.Abs(delta + 1.0) < 0.000001);
    }

    private static LtcFrameReceivedEventArgs Frame(LtcTimecode timecode, double detectedFps, double rawSeconds) =>
        new(timecode, detectedFps, rawSeconds);
}
