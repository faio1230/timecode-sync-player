using System.Diagnostics;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C1: ScenarioClock の 3 つの時間（UTC・単調ミリ秒・QPC）が 1:1 で進み、
/// 食い違いを作らないこと（設計: docs/design/v0.5.4-scenario-layer.md §2-1）。
/// </summary>
public class ScenarioClockTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Advance_MovesUtcMonotonicAndQpcTogether()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 50_000, qpcBase: 7_000_000);

        clock.Advance(TimeSpan.FromMilliseconds(250));

        clock.GetUtcNow().Should().Be(BaseUtc.AddMilliseconds(250), "UTC も同じだけ進む");
        clock.MonotonicMilliseconds.Should().Be(50_250);
        clock.Qpc.Should().Be(7_000_000 + (50_250 * Stopwatch.Frequency) / 1000,
            "QPC は単調ミリ秒から Stopwatch.Frequency で換算する");
    }

    [Fact]
    public void Advance_IsCumulativeAcrossWholeMilliseconds()
    {
        var clock = new ScenarioClock(BaseUtc);

        clock.AdvanceMilliseconds(100);
        clock.AdvanceMilliseconds(40);

        clock.MonotonicMilliseconds.Should().Be(10_140);
        clock.GetUtcNow().Should().Be(BaseUtc.AddMilliseconds(140));
    }

    [Fact]
    public void Advance_SubMillisecondOrNegative_ThrowsAndKeepsTheClock()
    {
        var clock = new ScenarioClock(BaseUtc);

        Action subMillisecond = () => clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2));
        Action negative = () => clock.Advance(TimeSpan.FromMilliseconds(-1));

        subMillisecond.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
        clock.MonotonicMilliseconds.Should().Be(10_000);
        clock.GetUtcNow().Should().Be(BaseUtc);
    }

    [Fact]
    public void Constructor_NormalizesUtcNowToUtc()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.FromHours(9)));

        clock.GetUtcNow().Should().Be(BaseUtc);
    }
}
