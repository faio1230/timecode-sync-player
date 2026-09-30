using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.0 S-4（検証機の失敗）: Single・RunThrough でクリップの外（出口の先）に LTC が保持されている間にトラックを
/// 読み込むと、規則 4 の入口の合わせが出口へシークする。その後は境界の保持（出口で一時停止）に入るはずが、
/// 入口の合わせはコーディネーターの「端へのシークの記録」を残さないため、保持の判定は位置が出口の ±2 映像フレーム
/// 以内に居る瞬間を読めたときしか入らない。60fps では窓が 33ms しかなく、着地が即時だと次の保持のフレーム
/// （LTC の 1 フレーム後）には窓を過ぎており、以後は誰も直さずに出口の先へ走り続けた（25.000 → 35.45）。
/// 対照: 着地が遅い（着地の直後のフレームで読む）場合と、窓の広い 25fps では保持に入る。
/// </summary>
public class SingleBoundaryHoldAfterHoldEntryTests
{
    private const double ClipIn = 5.0;
    private const double ClipOut = 25.0;
    private const double LtcFrameSeconds = 1.0 / 25.0;

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
            MediaInSeconds = ClipIn,
            MediaOutSeconds = ClipOut,
        };
        h.AddTrack("A", 0, 30);
        h.AddTrack("B", 0, 30);
        h.ChangeMode(SyncMode.Single);
        h.SetDurationSeconds(30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);
        return (h, clock);
    }

    private static void RunFor(SyncScenarioHarness h, ScenarioClock clock, int milliseconds, int stepMilliseconds = 40)
    {
        long end = clock.MonotonicMilliseconds + milliseconds;
        while (clock.MonotonicMilliseconds < end)
            h.AdvanceMilliseconds(stepMilliseconds);
    }

    /// <summary>
    /// 保持（35.0 の Duplicate）の中で次のトラックを読み込み、10 秒進める。読み込みの後の映像 fps と着地の遅れを
    /// 与える（偽プレイヤーの読み込みは fps をトラックの 25 に戻すので、読み込みの後に入れる）。
    /// <paramref name="ltcPhaseMilliseconds"/> は LTC の標本の位相（台本の開始を歩みの途中へずらす）。
    /// <paramref name="observationBeatMilliseconds"/> が 1 LTC フレームより長いときは、保持のフレームをその間隔で
    /// 1 枚ずつ届ける（間は無音。UI の停止などで観測の拍が伸びた場面。信号断の 250ms より短くする）。
    /// </summary>
    private static SyncScenarioHarness RunHoldEntryAfterLoad(
        double videoFps, double seekLandingDelaySeconds, int ltcPhaseMilliseconds, int stepMilliseconds,
        int observationBeatMilliseconds = 40)
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        long start = clock.MonotonicMilliseconds + ltcPhaseMilliseconds;
        h.Ltc.Normal(34.8, TimeSpan.FromMilliseconds(200), atMilliseconds: start);
        if (observationBeatMilliseconds <= 40)
        {
            h.Ltc.Duplicate(35.0, TimeSpan.FromSeconds(15));
        }
        else
        {
            for (int elapsed = 0; elapsed < 15_000; elapsed += observationBeatMilliseconds)
            {
                h.Ltc.Duplicate(35.0, TimeSpan.FromMilliseconds(40))
                    .Silence(TimeSpan.FromMilliseconds(observationBeatMilliseconds - 40));
            }
        }
        RunFor(h, clock, 1_500, stepMilliseconds);

        h.Playback.SeekLandingDelaySeconds = seekLandingDelaySeconds;
        h.Playback.LoadDurationSeconds = 0;
        h.ManualNextTrack();
        h.Playback.SetFps(videoFps);
        h.Operations.Clear();
        int eventsBefore = h.Events.Count;

        RunFor(h, clock, 10_000, stepMilliseconds);
        List<ScenarioEvent> seeksAfterLoad = h.Events.Skip(eventsBefore)
            .Where(e => e.Kind is "landing-seek" or "sync-seek").ToList();
        seeksAfterLoad.Should().NotBeEmpty();
        seeksAfterLoad[0].Kind.Should().Be("landing-seek", "前提: 読み込みの後の最初のシークは規則 4 の入口の合わせ");
        seeksAfterLoad[0].Value.Should().BeApproximately(ClipOut, 1e-6, "前提: 入口の合わせの目標は出口");
        return h;
    }

    /// <summary>
    /// 出口の近く: 保持の判定は観測の拍ごとなので、着地から最初の観測までに 1 拍（LTC の 1 フレームか、それより長い
    /// 歩み）ぶん進み得る。その先に保持の窓（2 映像フレーム）を足した範囲に留まる。
    /// </summary>
    private static void AssertHeldAtOut(SyncScenarioHarness h, double videoFps, int beatMilliseconds)
    {
        List<string> names = h.Operations.Select(o => o.Name).ToList();
        IReadOnlyList<double> seeks = h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();
        string because = $"seeks=[{string.Join(", ", seeks.Select(s => s.ToString("F3")))}] position={h.PlaybackSeconds:F3} paused={h.IsPaused}";

        names.Should().Contain("clip-end-hold",
            "入口の合わせで出口へ着いた後は、境界の保持（出口で一時停止）に入る（" + because + "）");
        double beatSeconds = Math.Max(LtcFrameSeconds, beatMilliseconds / 1000.0);
        h.PlaybackSeconds.Should().BeInRange(ClipOut, ClipOut + beatSeconds + 2.0 / videoFps,
            "保持の間は出口の近くに留まる（出口の先へ走り続けない。" + because + "）");
    }

    /// <summary>赤（C 型）: 60fps・着地の遅れ 0。入口の合わせの着地の直後に位置が窓（±2/60）を過ぎる。</summary>
    [Theory]
    [InlineData(0, 40)]
    [InlineData(20, 40)]
    [InlineData(0, 10)]
    [InlineData(5, 10)]
    public void HoldEntryAlignment_60fps_ImmediateLanding_EntersBoundaryHold(int ltcPhaseMilliseconds, int stepMilliseconds)
    {
        SyncScenarioHarness h = RunHoldEntryAfterLoad(60, 0, ltcPhaseMilliseconds, stepMilliseconds);
        AssertHeldAtOut(h, 60, stepMilliseconds);
    }

    /// <summary>対照（B 型）: 60fps・着地の遅れ 0.3 秒。着地の直後のフレームで位置が窓の中にあり、保持に入る。</summary>
    [Theory]
    [InlineData(0, 40)]
    [InlineData(20, 40)]
    public void HoldEntryAlignment_60fps_DelayedLanding_EntersBoundaryHold(int ltcPhaseMilliseconds, int stepMilliseconds)
    {
        SyncScenarioHarness h = RunHoldEntryAfterLoad(60, 0.3, ltcPhaseMilliseconds, stepMilliseconds);
        AssertHeldAtOut(h, 60, stepMilliseconds);
    }

    /// <summary>対照: 25fps（窓 ±80ms）・着地の遅れ 0。次の保持のフレームでも位置が窓の中にあり、保持に入る。</summary>
    [Theory]
    [InlineData(0, 40)]
    [InlineData(20, 40)]
    public void HoldEntryAlignment_25fps_ImmediateLanding_EntersBoundaryHold(int ltcPhaseMilliseconds, int stepMilliseconds)
    {
        SyncScenarioHarness h = RunHoldEntryAfterLoad(25, 0, ltcPhaseMilliseconds, stepMilliseconds);
        AssertHeldAtOut(h, 25, stepMilliseconds);
    }

    /// <summary>
    /// 30fps（窓 ±66ms）・着地の遅れ 0 で観測の拍が長い（WASAPI の約 50ms の 2 枚まとめで 100ms、UI の停止などで
    /// 約 120ms）。着地から次の保持のフレームまでに窓を過ぎる。
    /// </summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(0, 120)]
    [InlineData(20, 120)]
    public void HoldEntryAlignment_30fps_LongObservationBeat_EntersBoundaryHold(int ltcPhaseMilliseconds, int beatMilliseconds)
    {
        SyncScenarioHarness h = RunHoldEntryAfterLoad(30, 0, ltcPhaseMilliseconds, 10, beatMilliseconds);
        AssertHeldAtOut(h, 30, beatMilliseconds);
    }
}
