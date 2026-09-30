using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C3: LTC 台本の各区間（Normal／Duplicate／Jump／Silence）が
/// 予定時刻どおりにフレームを並べること（設計: docs/design/v0.5.4-scenario-layer.md §2-3）。
/// v0.6.1 段 A: 台本は値と fps だけを並べる（状態・同期の適用の可否は受け手の層 1 の診断が決めるので、ここでは見ない）。
/// </summary>
public class LtcScriptTests
{
    private static (LtcScript Script, List<(LtcScriptFrame Frame, long At)> Frames) Create()
    {
        var frames = new List<(LtcScriptFrame, long)>();
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
        frames[0].Frame.Seconds.Should().BeApproximately(5.0, 1e-9);
        frames[1].Frame.Seconds.Should().BeApproximately(5.04, 1e-9);
        frames[2].Frame.Seconds.Should().BeApproximately(5.08, 1e-9);
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
    public void Duplicate_HoldsTheValue()
    {
        (LtcScript script, var frames) = Create();

        script.Duplicate(7.0, TimeSpan.FromMilliseconds(80));
        script.AdvanceTime(TimeSpan.FromMilliseconds(100));

        frames.Should().HaveCount(2);
        frames.Should().OnlyContain(f => f.Frame.Seconds == 7.0);
    }

    [Fact]
    public void Jump_EmitsTheConfiguredCountOfTheValue()
    {
        (LtcScript script, var frames) = Create();

        script.Jump(12.0, count: 2);
        script.AdvanceTime(TimeSpan.FromMilliseconds(100));

        frames.Should().HaveCount(2);
        frames.Select(f => f.At).Should().Equal(10_000L, 10_040L);
        frames.Should().OnlyContain(f => f.Frame.Seconds == 12.0);
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
    public void Append_ContinuesAfterThePreviousSegment()
    {
        (LtcScript script, var frames) = Create();

        script.Normal(20.0, TimeSpan.FromMilliseconds(40))
            .Duplicate(20.0, TimeSpan.FromMilliseconds(200))
            .Jump(23.0);
        script.AdvanceTime(TimeSpan.FromMilliseconds(400));

        frames.Select(f => f.At).Should().Equal(10_000L, 10_040L, 10_080L, 10_120L, 10_160L, 10_200L, 10_240L);
        frames.Select(f => f.Frame.Seconds).Should().Equal(20.0, 20.0, 20.0, 20.0, 20.0, 20.0, 23.0);
    }
}
