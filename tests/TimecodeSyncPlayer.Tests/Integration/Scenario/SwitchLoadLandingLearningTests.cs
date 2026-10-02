using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.3 段 2（docs/design/v0.6.3-chase-cleanup.md 1 節・13 節）: Continue の切替のロードの着地（目標の無い着地）の遅れが
/// 先行量 c に入り、切替の直後に +・−・− の relocate が 3 本出る連鎖。規則 3 は「c はシークの所要の学習値、学習前は 0」。
/// 偽の再生 API（<see cref="ScenarioPlayback"/>）のロードの所要（LoadDurationSeconds）でロードの着地の遅れを、
/// シークの着地の遅れ（SeekLandingDelaySeconds）で relocate の着地の遅れを与える。
/// </summary>
[Collection("Serilog global logger")]
public class SwitchLoadLandingLearningTests
{
    private const long BaseMilliseconds = 50_000;
    private const double ToleranceSeconds = 0.24;

    private readonly ITestOutputHelper _output;

    public SwitchLoadLandingLearningTests(ITestOutputHelper output) => _output = output;

    /// <summary>切替の後の relocate（"Continue mode: sync seek" の行）。Delta は行の delta（正 = 前向き、映像が遅れている）。</summary>
    private sealed record Relocate(long AtMs, double Target, double Delta, double LookaheadMs);

    /// <summary>このテストのスレッドの relocate・着地・ロードの着地の行を、台本の 0 からの時刻つきで拾う。</summary>
    private sealed class LineSink : ILogEventSink, IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private readonly Func<long> _now;
        public List<string> Lines { get; } = new();

        public LineSink(Func<long> now)
        {
            _now = now;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(this).CreateLogger();
        }

        public void Emit(LogEvent logEvent)
        {
            if (Environment.CurrentManagedThreadId != _owner)
                return;
            string message = logEvent.RenderMessage();
            if (message.StartsWith("Continue mode: sync seek", StringComparison.Ordinal) ||
                message.StartsWith("Continue mode: switching", StringComparison.Ordinal) ||
                message.StartsWith("File load landing", StringComparison.Ordinal) ||
                message.StartsWith("sync.gate new-landing", StringComparison.Ordinal))
                lock (Lines) Lines.Add($"{_now()}ms {message}");
        }

        public void Dispose() => Log.Logger = _previous;

        /// <summary>M4 への切替の行より後の relocate。</summary>
        public List<Relocate> Relocates()
        {
            lock (Lines)
            {
                return Lines
                    .SkipWhile(l => !l.Contains("switching to track \"M4\"", StringComparison.Ordinal))
                    .Where(l => l.Contains("Continue mode: sync seek", StringComparison.Ordinal))
                    .Select(l => new Relocate(
                        long.Parse(l[..l.IndexOf("ms ", StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture),
                        Field(l, "target"), Field(l, "delta"), Field(l, "lookaheadMs")))
                    .ToList();
            }
        }

        private static double Field(string line, string name) =>
            double.Parse(System.Text.RegularExpressions.Regex.Match(line, " " + name + @"=(-?[\d.]+)").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record Outcome(List<Relocate> Relocates, double FirstLandingError, double FinalError)
    {
        public string Summary =>
            $"relocates=[{string.Join(", ", Relocates.Select(r => $"{r.AtMs}ms {r.Target:F3}(delta {r.Delta:+0.000;-0.000}, lookahead {r.LookaheadMs:F0}ms)"))}] " +
            $"firstLandingError={FirstLandingError:+0.000;-0.000} finalError={FinalError:+0.000;-0.000}";
    }

    /// <summary>
    /// M3（タイムライン 0〜10 秒）→ M4（10〜20 秒）の Continue を LTC 5 → 17 秒で流す。切替の前（LTC 9.5 秒）にロードの所要を
    /// <paramref name="loadSeconds"/> にし、切替の後の relocate の n 本目の着地の遅れを <paramref name="seekDelays"/>[n]（最後の値を以後も使う）にする。
    /// </summary>
    private Outcome Run(double loadSeconds, double[] seekDelays)
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
        };
        h.AddTrack("M3", 0, 10);
        h.AddTrack("M4", 10, 10);
        h.ManualPlay();
        h.AdvancePlayback(5.0);
        long start = clock.MonotonicMilliseconds;
        using var sink = new LineSink(() => clock.MonotonicMilliseconds - start);
        h.Ltc.Normal(5.0, TimeSpan.FromSeconds(12));
        long end = h.Ltc.NextMilliseconds;

        bool armed = false;
        int switchLoadIndex = -1;
        int seeksSeen = 0;
        var relocates = new List<Relocate>();
        double firstLandingError = double.NaN;
        while (clock.MonotonicMilliseconds < end)
        {
            long t = clock.MonotonicMilliseconds - start;
            double ltcNow = 5.0 + t / 1000.0;
            if (!armed && ltcNow >= 9.5)
            {
                h.Playback.LoadDurationSeconds = loadSeconds;
                h.Playback.SeekLandingDelaySeconds = seekDelays[0];
                armed = true;
            }
            if (switchLoadIndex < 0)
                switchLoadIndex = h.Operations.FindIndex(o => o.Name.StartsWith("loadfile", StringComparison.Ordinal) && o.Text == "C:/M4.mp4");

            h.AdvanceMilliseconds(40);

            if (switchLoadIndex < 0)
            {
                switchLoadIndex = h.Operations.FindIndex(o => o.Name.StartsWith("loadfile", StringComparison.Ordinal) && o.Text == "C:/M4.mp4");
                if (switchLoadIndex >= 0)
                    seeksSeen = h.Operations.Skip(switchLoadIndex).Count(o => o.Name == "seek");
                continue;
            }

            int seeksAfterSwitch = h.Operations.Skip(switchLoadIndex).Count(o => o.Name == "seek");
            while (seeksSeen < seeksAfterSwitch)
            {
                seeksSeen++;
                h.Playback.SeekLandingDelaySeconds = seekDelays[Math.Min(seeksSeen, seekDelays.Length - 1)];
            }
            relocates = sink.Relocates();
            if (relocates.Count > 0 && double.IsNaN(firstLandingError) && !h.Playback.HasPendingSeek)
                firstLandingError = h.Playback.PositionSeconds - (5.0 + (clock.MonotonicMilliseconds - start) / 1000.0 - 10.0);
        }
        double finalError = h.Playback.PositionSeconds - (5.0 + (clock.MonotonicMilliseconds - start) / 1000.0 - 10.0);
        switchLoadIndex.Should().BeGreaterThanOrEqualTo(0, "前提: M4 へ切り替えた");
        var outcome = new Outcome(relocates, firstLandingError, finalError);
        _output.WriteLine(outcome.Summary);
        foreach (string line in sink.Lines.Where(l => !l.Contains("new-landing") || relocates.Count > 0))
            _output.WriteLine("  " + line);
        return outcome;
    }

    /// <summary>
    /// T1（開発機の実測の形、13 節）: ロード 0.30 秒、切替の後のシークの着地は 1 本目 0.003・2 本目 0.04・3 本目 0.11 秒。
    /// ロードの遅れを c に入れると、1 本目が 0.3 秒先へ行き過ぎ、速い着地は学習されないので c が下がらず +・−・− の 3 本になる。
    /// </summary>
    [Fact]
    public void T1_SwitchLoadLanding_IsNotLearned_AtMostOneRelocateAfterTheSwitch()
    {
        Outcome outcome = Run(loadSeconds: 0.30, seekDelays: [0.003, 0.04, 0.11]);

        outcome.Relocates.Count.Should().BeLessThanOrEqualTo(1,
            "切替の後の relocate は 1 本以内（ロードの着地の遅れは c に入れない）。" + outcome.Summary);
        if (outcome.Relocates.Count > 0)
            Math.Abs(outcome.FirstLandingError).Should().BeLessThanOrEqualTo(ToleranceSeconds,
                "最初の着地の後の |誤差| は tol 以内。" + outcome.Summary);
        Math.Abs(outcome.FinalError).Should().BeLessThanOrEqualTo(ToleranceSeconds, outcome.Summary);
    }

    /// <summary>
    /// T2（重い素材の型、1-3 の (a) の副作用の上限）: ロード 0.30 秒、シークの着地は毎回 0.25 秒。ロード直後の 1 本目は c = 0 で
    /// 着地ぶん遅れうるので、前向きの 2 本目は 1 本まで（前向きの relocate は合わせて 2 本まで）。
    /// </summary>
    [Fact]
    public void T2_HeavyMedia_AtMostOneSecondForwardRelocateAfterTheSwitch()
    {
        Outcome outcome = Run(loadSeconds: 0.30, seekDelays: [0.25]);

        outcome.Relocates.Count(r => r.Delta > 0).Should().BeLessThanOrEqualTo(2,
            "ロード直後の前向きの 2 本目は 1 本まで。" + outcome.Summary);
        outcome.Relocates.Count.Should().BeLessThanOrEqualTo(2, outcome.Summary);
        Math.Abs(outcome.FinalError).Should().BeLessThanOrEqualTo(ToleranceSeconds, outcome.Summary);
    }

    /// <summary>
    /// T3（13 節の 1080p の悪化、TSP-Fable の判断）: 4K 相当の素材（シークの着地 0.33 秒を学習済み）から 1080p の素材へ切り替えるとき、
    /// 切替のロード位置に前の素材の c を足さない（c はシークの所要の学習値で、素材ごと。ロードで捨てて学習前は 0）。
    /// 足すと、着地の速い 1080p で 0.33 秒先へ読み込んで行き過ぎ、後ろ向きの relocate が出る。
    /// </summary>
    [Fact]
    public void T3_SwitchLoadPosition_DoesNotCarryThePreviousMaterialsSeekCost()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
        };
        h.AddTrack("M4K", 0, 10);
        h.AddTrack("M1080", 10, 10);
        h.ManualPlay();
        h.AdvancePlayback(2.0);
        long start = clock.MonotonicMilliseconds;
        using var sink = new LineSink(() => clock.MonotonicMilliseconds - start);
        h.Playback.SeekLandingDelaySeconds = 0.33;            // 4K 相当: シークの着地 0.33 秒
        h.Ltc.Normal(2.0, TimeSpan.FromSeconds(2));
        h.Ltc.Normal(6.0, TimeSpan.FromSeconds(6));           // 2 秒先へ飛ぶ（relocate して c = 0.33 を学習する）
        long end = h.Ltc.NextMilliseconds;
        double? learnedBeforeSwitch = null;
        while (clock.MonotonicMilliseconds < end)
        {
            if (!h.Operations.Any(o => o.Name.StartsWith("loadfile", StringComparison.Ordinal) && o.Text == "C:/M1080.mp4"))
                learnedBeforeSwitch = h.SeekState.LearnedSeekDurationSeconds;
            h.AdvanceMilliseconds(40);
        }

        ScenarioPlaybackOperation switchLoad = h.Operations.Single(
            o => o.Name.StartsWith("loadfile", StringComparison.Ordinal) && o.Text == "C:/M1080.mp4");
        string switching = sink.Lines.Single(l => l.Contains("switching to track \"M1080\"", StringComparison.Ordinal));
        double mediaPos = double.Parse(
            System.Text.RegularExpressions.Regex.Match(switching, @"at media position (-?[\d.]+)s").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        _output.WriteLine($"learnedBeforeSwitch={learnedBeforeSwitch:F3} mediaPos={mediaPos:F3} loadPosition={switchLoad.Value:F3}");
        foreach (string line in sink.Lines)
            _output.WriteLine("  " + line);

        learnedBeforeSwitch.Should().BeApproximately(0.33, 0.05, "前提: 前の素材でシークの所要 c を学習した");
        switchLoad.Value.Should().BeApproximately(mediaPos, 1e-6,
            "切替のロード位置は素材位置そのもの（D7-a の補償は既定 0）。前の素材の c を足さない");
    }
}
