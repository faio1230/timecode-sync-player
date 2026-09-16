using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T2 段 2/3: フレーム終端から受信ハンドラまでの経過（age）を同期値に足す切替
/// （TCS_LTC_SAMPLE_CLOCK、段 3 で既定 on。off 指定時だけ無効）。
/// Single モードのシーク先（= 同期目標）で観測する。
/// </summary>
public class T2SampleClockTests
{
    private static long Ticks(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    private static SyncScenarioHarness ArrangeSingle(long[] nowQpc, bool sampleClockEnabled)
    {
        var harness = new SyncScenarioHarness(
            sampleClockEnabled: sampleClockEnabled, getQpc: () => nowQpc[0]);
        harness.AddTrack("track", 0);
        harness.ManualPlay();
        harness.ChangeMode(SyncMode.Single);
        harness.Operations.Clear();
        return harness;
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData(" off ", false)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("true", true)]
    public void IsSampleClockEnabled_OnlyOffDisables(string? value, bool expected)
        => LtcSyncController.IsSampleClockEnabled(value).Should().Be(expected);

    [Fact]
    public void SampleClockEnabled_AddsAgeToSyncTarget()
    {
        long[] now = [5_000_000_000];
        SyncScenarioHarness harness = ArrangeSingle(now, sampleClockEnabled: true);

        harness.SupplyLtcFrame(3.0, frameEndTimestamp: now[0] - Ticks(0.1));

        var seek = harness.Operations.Should().ContainSingle(o => o.Name == "seek").Subject;
        seek.Value.Should().BeApproximately(3.1, 1e-9);
    }

    [Fact]
    public void SampleClockDisabled_DoesNotAddAge()
    {
        long[] now = [5_000_000_000];
        SyncScenarioHarness harness = ArrangeSingle(now, sampleClockEnabled: false);

        harness.SupplyLtcFrame(3.0, frameEndTimestamp: now[0] - Ticks(0.1));

        var seek = harness.Operations.Should().ContainSingle(o => o.Name == "seek").Subject;
        seek.Value.Should().BeApproximately(3.0, 1e-9);
    }

    [Fact]
    public void SampleClockOutOfRangeAge_IsNotAdded()
    {
        // 0.5 秒超（停止・時計の不一致）は足さない。
        long[] past = [5_000_000_000];
        SyncScenarioHarness late = ArrangeSingle(past, sampleClockEnabled: true);
        late.SupplyLtcFrame(3.0, frameEndTimestamp: past[0] - Ticks(0.6));
        late.Operations.Should().ContainSingle(o => o.Name == "seek").Subject.Value
            .Should().BeApproximately(3.0, 1e-9);

        // 負の age（終端が未来）も足さない。
        long[] future = [5_000_000_000];
        SyncScenarioHarness early = ArrangeSingle(future, sampleClockEnabled: true);
        early.SupplyLtcFrame(3.0, frameEndTimestamp: future[0] + Ticks(0.1));
        early.Operations.Should().ContainSingle(o => o.Name == "seek").Subject.Value
            .Should().BeApproximately(3.0, 1e-9);
    }

    [Fact]
    public void PendingSync_RecomputesAgeWhenUsedFromTick()
    {
        long[] now = [7_000_000_000];
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new SyncScenarioHarness(
            clock, sampleClockEnabled: true, getQpc: () => now[0]);
        harness.AddTrack("track", 0);
        harness.ManualPlay();
        harness.ChangeMode(SyncMode.Single);
        harness.Operations.Clear();

        // ネイティブシーク中は同期要求が保留になる。
        harness.NativeSeeking = true;
        harness.SupplyLtcFrame(3.0, frameEndTimestamp: now[0] - Ticks(0.1));
        harness.Operations.Should().NotContain(o => o.Name == "seek");

        // 使う時点の age（0.2 秒）で実効値を取り直す。
        now[0] += Ticks(0.1);
        harness.NativeSeeking = false;
        harness.Tick100Milliseconds();

        var seek = harness.Operations.Should().ContainSingle(o => o.Name == "seek").Subject;
        seek.Value.Should().BeApproximately(3.2, 1e-9);
    }

    [Fact]
    public void ProcessedFrameWithoutSourceFrame_SkipsAgeAndDoesNotCrash()
    {
        long[] now = [9_000_000_000];
        SyncScenarioHarness harness = ArrangeSingle(now, sampleClockEnabled: true);

        // sourceFrame 無しの公開経路（フレーム終端が無い）では age を足さない。
        harness.SupplyLtc(3.0);

        var seek = harness.Operations.Should().ContainSingle(o => o.Name == "seek").Subject;
        seek.Value.Should().BeApproximately(3.0, 1e-9);
    }
}
