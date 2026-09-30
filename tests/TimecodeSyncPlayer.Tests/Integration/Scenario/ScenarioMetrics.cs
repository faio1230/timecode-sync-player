using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C4: 観測イベントから指標を求める（設計: docs/design/v0.5.4-scenario-layer.md §2-4）。
/// 定義は実機 E2E に合わせる（hold-landing は LtcScenarioE2ETests.cs:2213-2220、
/// jump-black は状態の代理、hold-pause は同 :823-839、follow は LtcFollowSeries）。
/// ここは計算だけで、失敗の条件は持たない。
/// </summary>
internal sealed record ScenarioHoldLanding(
    string Name,
    long JumpAtMilliseconds,
    long? SeekAtMilliseconds,
    double JumpDistanceSeconds,
    double? LandingLatencySeconds,
    bool LatencyOverBudget);

internal sealed record ScenarioJumpBlack(
    long FromMilliseconds,
    long ToMilliseconds,
    int Samples,
    int BlackTicks,
    double BlackSeconds);

internal sealed record ScenarioHoldPause(
    long HoldAtMilliseconds,
    long? PauseAtMilliseconds,
    double? PauseLatencySeconds);

internal sealed record ScenarioFollowResult(int Samples, double MaxErrorSeconds, bool IsFollowing);

internal static class ScenarioMetrics
{
    public const double FollowToleranceSeconds = 0.3;
    private const long SeekSearchWindowMilliseconds = 5_000;
    private const double LtcFps = 25.0;

    /// <summary>Jump フレームの到着から次のシーク発行まで（hold-landing。E2E と同じ定義）。</summary>
    public static IReadOnlyList<ScenarioHoldLanding> HoldLandings(SyncScenarioHarness harness)
    {
        List<ScenarioEvent> events = harness.Events;
        var landings = new List<ScenarioHoldLanding>();
        for (int index = 0; index < events.Count; index++)
        {
            ScenarioEvent current = events[index];
            if (current.Kind != "ltc-frame" || current.Detail != nameof(TimecodeFrameDiagnosticStatus.Jump))
                continue;

            double previous = PreviousLtcSeconds(events, index);
            double distance = double.IsFinite(previous) && current.Value.HasValue
                ? Math.Abs(current.Value.Value - previous)
                : double.NaN;

            ScenarioEvent? seek = events
                .Skip(index + 1)
                .TakeWhile(e => e.AtMilliseconds - current.AtMilliseconds <= SeekSearchWindowMilliseconds)
                .FirstOrDefault(e => e.Kind == "seek");
            double? latency = seek is null
                ? null
                : (seek.AtMilliseconds - current.AtMilliseconds) / 1000.0;

            double tolerance = SyncToleranceSeconds(harness);
            bool overBudget =
                double.IsFinite(distance) &&
                distance > 4 * tolerance &&
                latency > 1.0;

            landings.Add(new ScenarioHoldLanding(
                $"jump-{landings.Count + 1:D2}",
                current.AtMilliseconds,
                seek?.AtMilliseconds,
                distance,
                latency,
                overBudget));
        }
        return landings;
    }

    /// <summary>jump-black（状態の代理）: 区間内の tick のうち RenderSurface が Black だったもの。</summary>
    public static ScenarioJumpBlack JumpBlack(
        SyncScenarioHarness harness, long fromMilliseconds, long toMilliseconds)
    {
        List<ScenarioEvent> ticks = harness.Events
            .Where(e => e.Kind == "tick" &&
                        e.AtMilliseconds >= fromMilliseconds &&
                        e.AtMilliseconds <= toMilliseconds)
            .ToList();
        int blackTicks = ticks.Count(t => t.Detail == nameof(ScenarioRenderSurface.Black));

        double blackSeconds = 0;
        for (int i = 1; i < ticks.Count; i++)
        {
            if (ticks[i - 1].Detail == nameof(ScenarioRenderSurface.Black))
                blackSeconds += (ticks[i].AtMilliseconds - ticks[i - 1].AtMilliseconds) / 1000.0;
        }

        return new ScenarioJumpBlack(
            fromMilliseconds, toMilliseconds, ticks.Count, blackTicks, blackSeconds);
    }

    /// <summary>hold-pause: 保持の開始から次の pause イベントまで（E2E R-1/R-2 と同じ向き）。</summary>
    public static ScenarioHoldPause HoldPause(SyncScenarioHarness harness, long holdAtMilliseconds)
    {
        ScenarioEvent? pause = harness.Events.FirstOrDefault(
            e => e.Kind == "pause" && e.AtMilliseconds >= holdAtMilliseconds);
        double? latency = pause is null
            ? null
            : (pause.AtMilliseconds - holdAtMilliseconds) / 1000.0;
        return new ScenarioHoldPause(holdAtMilliseconds, pause?.AtMilliseconds, latency);
    }

    /// <summary>follow: 区間の tick ごとに「直近の LTC と再生位置」を対にして写像の誤差を出す。</summary>
    public static ScenarioFollowResult Follow(
        SyncScenarioHarness harness,
        long fromMilliseconds,
        long toMilliseconds,
        double timelineStartSeconds,
        double mediaInSeconds,
        double toleranceSeconds = FollowToleranceSeconds)
    {
        var samples = new List<(double Ltc, double Position)>();
        double ltc = double.NaN;
        foreach (ScenarioEvent e in harness.Events)
        {
            if (e.Kind == "ltc-frame" && e.Value.HasValue)
                ltc = e.Value.Value;
            if (e.Kind == "tick" &&
                e.AtMilliseconds >= fromMilliseconds && e.AtMilliseconds <= toMilliseconds &&
                e.Value.HasValue && double.IsFinite(ltc))
            {
                samples.Add((ltc, e.Value.Value));
            }
        }

        double maxError = LtcFollowSeries.MaxErrorSeconds(samples, timelineStartSeconds, mediaInSeconds);
        bool following = LtcFollowSeries.IsFollowing(
            samples, timelineStartSeconds, mediaInSeconds, toleranceSeconds);
        return new ScenarioFollowResult(samples.Count, maxError, following);
    }

    public static int SeekCount(SyncScenarioHarness harness) =>
        harness.Events.Count(e => e.Kind == "seek");

    /// <summary>段 0 の「同期シーク」（coordinator 経由のシーク）。</summary>
    public static int SyncSeekCount(SyncScenarioHarness harness) =>
        harness.Events.Count(e => e.Kind == "sync-seek");

    /// <summary>段 0 の「着地シーク」（保持着地・Jump 補正。controller 経由）。</summary>
    public static int LandingSeekCount(SyncScenarioHarness harness) =>
        harness.Events.Count(e => e.Kind == "landing-seek");

    /// <summary>E2E と同じ 1 フレームの tolerance（映像と LTC の大きい方）。</summary>
    public static double SyncToleranceSeconds(SyncScenarioHarness harness)
    {
        double videoFps = harness.Playback.Fps > 0 ? harness.Playback.Fps : 30.0;
        return Math.Max(1.0 / videoFps, 1.0 / LtcFps);
    }

    private static double PreviousLtcSeconds(List<ScenarioEvent> events, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (events[i].Kind == "ltc-frame" && events[i].Value.HasValue)
                return events[i].Value!.Value;
        }
        return double.NaN;
    }
}
