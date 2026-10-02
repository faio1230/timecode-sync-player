using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.4 設計書 3-1（#12 C-2 の黒の診断回）: hold の判定が失敗した後も数秒採取を続ける試験だけの口。
/// 既定（環境変数なし）では採取しない。合否は変えない（判定は採取の後にこれまでどおり行う）。
/// </summary>
public class HoldKeepSamplingTests
{
    private static Func<string, string?> Env(string? value) =>
        name => name == HoldKeepSampling.EnvironmentVariable ? value : null;

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void IsEnabled_OnlyWhenTheVariableIsOne(string? value, bool expected) =>
        HoldKeepSampling.IsEnabled(Env(value)).Should().Be(expected);

    [Fact]
    public void VariableName_SaysTest() =>
        HoldKeepSampling.EnvironmentVariable.Should().Contain("TEST", "試験だけの口は名前に TEST を入れる（設計書 7 節）");

    [Theory]
    [InlineData(false, "c2-12", true, false)]   // 既定（環境変数なし）: 今と同じで採取しない
    [InlineData(true, "c2-12", false, false)]   // 判定が通った hold では採取しない
    [InlineData(true, "a1-01", true, false)]    // C-2 以外の hold では採取しない
    [InlineData(true, "c2-12", true, true)]
    public void ShouldRun_OnlyForAFailedC2HoldWithTheVariable(bool enabled, string holdName, bool holdFailed, bool expected) =>
        HoldKeepSampling.ShouldRun(enabled, holdName, holdFailed).Should().Be(expected);

    [Fact]
    public void Collect_SamplesEveryIntervalUntilTheDuration()
    {
        double now = 0;
        var sleeps = new List<TimeSpan>();
        IReadOnlyList<double> samples = HoldKeepSampling.Collect(
            take: () => now,
            elapsedSeconds: () => now,
            sleep: interval => { sleeps.Add(interval); now += interval.TotalSeconds; });

        samples.Should().HaveCount(25, "5 秒を 200ms ごと");
        samples[0].Should().BeApproximately(0.2, 1e-9);
        samples[^1].Should().BeApproximately(5.0, 1e-9);
        sleeps.Should().OnlyContain(interval => interval == HoldKeepSampling.Interval);
    }
}
