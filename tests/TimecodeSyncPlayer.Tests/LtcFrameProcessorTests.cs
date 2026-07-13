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

    private static LtcFrameReceivedEventArgs Frame(LtcTimecode timecode, double detectedFps, double rawSeconds) =>
        new(timecode, detectedFps, rawSeconds);
}
