using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C3: LTC 台本の各区間（Normal／Duplicate／Jump／Silence／Raw）が
/// 予定時刻どおりにフレームを並べること（設計: docs/design/v0.5.4-scenario-layer.md §2-3）。
/// </summary>
public class LtcScriptTests
{
    private static (LtcScript Script, List<(LtcFrameProcessingResult Frame, long At)> Frames) Create()
    {
        var frames = new List<(LtcFrameProcessingResult, long)>();
        var script = new LtcScript((frame, at) => frames.Add((frame, at)), startMilliseconds: 10_000);
        return (script, frames);
    }

    [Fact]
    public void Normal_EmitsFramesOnTheGridWhileAdvancing()
    {
        (LtcScript script, var frames) = Create();

        script.Normal(5.0, TimeSpan.FromMilliseconds(120));
        script.AdvanceTime(TimeSpan.FromMilliseconds(100));

        frames.Select(f => f.At).Should().Equal(10_000L, 10_040L, 10_080L);
        frames[0].Frame.ResolvedSeconds.Should().BeApproximately(5.0, 1e-9);
        frames[1].Frame.ResolvedSeconds.Should().BeApproximately(5.04, 1e-9);
        frames[2].Frame.ResolvedSeconds.Should().BeApproximately(5.08, 1e-9);
        frames.Should().OnlyContain(f => f.Frame.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Normal);
        frames.Should().OnlyContain(f => f.Frame.ShouldApplySync, "Normal は同期に適用する");
        script.NextMilliseconds.Should().Be(10_120, "区間の終わりが次の開始になる");
    }

    [Fact]
    public void Advance_EmitsWithScheduledTimes_NotTheAdvanceTime()
    {
        (LtcScript script, var frames) = Create();
        script.Normal(1.0, TimeSpan.FromMilliseconds(120));

        script.AdvanceTime(TimeSpan.FromMilliseconds(100));

        // まとめて進めても各フレームは予定時刻で届く。
        frames.Select(f => f.At).Should().Equal(new long[] { 10_000L, 10_040L, 10_080L });
    }

    [Fact]
    public void Duplicate_HoldsTheValueWithHeldStatus()
    {
        (LtcScript script, var frames) = Create();

        script.Duplicate(7.0, TimeSpan.FromMilliseconds(80));
        script.AdvanceTime(TimeSpan.FromMilliseconds(100));

        frames.Should().HaveCount(2);
        frames.Should().OnlyContain(f => f.Frame.ResolvedSeconds == 7.0);
        frames.Should().OnlyContain(f => f.Frame.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Duplicate);
        frames.Should().OnlyContain(f => !f.Frame.ShouldApplySync, "保持は同期に適用しない");
    }

    [Fact]
    public void Jump_EmitsTheConfiguredCountAsJump()
    {
        (LtcScript script, var frames) = Create();

        script.Jump(12.0, count: 2);
        script.AdvanceTime(TimeSpan.FromMilliseconds(100));

        frames.Should().HaveCount(2);
        frames.Select(f => f.At).Should().Equal(10_000L, 10_040L);
        frames.Should().OnlyContain(f => f.Frame.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Jump);
        frames.Should().OnlyContain(f => !f.Frame.ShouldApplySync, "Jump 1 枚は D27-b の復帰用に同期へは適用しない");
    }

    [Fact]
    public void Silence_EmitsNothingAndMovesTheCursor()
    {
        (LtcScript script, var frames) = Create();

        script.Silence(TimeSpan.FromMilliseconds(500));
        script.AdvanceTime(TimeSpan.FromMilliseconds(600));

        frames.Should().BeEmpty();
        script.NextMilliseconds.Should().Be(10_500);
    }

    [Fact]
    public void Raw_EmitsWithTheGivenStatusAndApplyFlag()
    {
        (LtcScript script, var frames) = Create();

        script.Raw(TimecodeFrameDiagnosticStatus.Normal, 20.0, count: 1, shouldApplySync: true);
        script.AdvanceTime(TimeSpan.FromMilliseconds(100));

        frames.Should().ContainSingle();
        frames[0].Frame.Diagnostic.Status.Should().Be(TimecodeFrameDiagnosticStatus.Normal);
        frames[0].Frame.ShouldApplySync.Should().BeTrue();
        frames[0].Frame.ResolvedSeconds.Should().Be(20.0);
    }

    [Fact]
    public void Append_ContinuesAfterThePreviousSegment()
    {
        (LtcScript script, var frames) = Create();

        script.Raw(TimecodeFrameDiagnosticStatus.Normal, 20.0, count: 1, shouldApplySync: true)
            .Duplicate(20.0, TimeSpan.FromMilliseconds(200))
            .Jump(23.0);
        script.AdvanceTime(TimeSpan.FromMilliseconds(400));

        frames.Select(f => f.Frame.Diagnostic.Status).Should().Equal(
            TimecodeFrameDiagnosticStatus.Normal,
            TimecodeFrameDiagnosticStatus.Duplicate,
            TimecodeFrameDiagnosticStatus.Duplicate,
            TimecodeFrameDiagnosticStatus.Duplicate,
            TimecodeFrameDiagnosticStatus.Duplicate,
            TimecodeFrameDiagnosticStatus.Duplicate,
            TimecodeFrameDiagnosticStatus.Jump);
        frames[^1].Frame.ResolvedSeconds.Should().Be(23.0);
    }
}
