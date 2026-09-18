using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// LTC 同期の検証行列（docs/LTC-SYNC-VERIFICATION-MATRIX-2026-09-17.md）3 節の E2E シナリオ。
/// VB-CABLE ループが無い環境ではスキップする。
///
/// 素材は固定しない: プロジェクトは TIMECODE_LTC_SCENARIO_PROJECT（.tsp）で差し替えられ、
/// 未設定なら色素材の Fixtures/ltc-scenario.tsp を artifacts/media へコピーして使う。
/// 判定は既知の色ではなく、テスト最初に一時停止シークで採った参照フレーム（各トラックの
/// 冒頭・最終）との一致で行う。中間位置は「位置が目標 ±0.3 秒に入り、進行する」だけを要求し、
/// 期待色などの観測値はジャーナルに残す。黒/冒頭/最終/Freeze/トラック境界/ギャップは
/// 参照一致（距離 &lt; 60・最近傍・画素差分 &lt; 12/255）または黒判定（平均輝度 &lt; 8/255 かつ
/// 黒画素 >= 99%）を要求する。
///
/// 実素材のファイル名・作品名はジャーナル・参照画像名に出さない。トラックは位置で
/// A/B/C（既定プロジェクト）または M1..M7（TIMECODE_LTC_SCENARIO_PROJECT）と呼ぶ。
/// 証跡は artifacts/ltc-scenarios/&lt;testId&gt;-&lt;timestamp&gt;/（TIMECODE_LTC_SCENARIO_REPORT_DIR で上書き可）。
/// ストレスの周回数は TIMECODE_LTC_SCENARIO_CYCLES で上書きできる。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class LtcScenarioE2ETests
{
    private const int LtcFps = 25;
    private const string ProjectVariable = "TIMECODE_LTC_SCENARIO_PROJECT";
    private const string ReportVariable = "TIMECODE_LTC_SCENARIO_REPORT_DIR";
    private const string CyclesVariable = "TIMECODE_LTC_SCENARIO_CYCLES";
    private const double PositionToleranceSeconds = 0.3;

    // S-2: 停止モードは停止時に同期コントローラが保持値へ着地シークを 1 回発行するため、
    // 同期コーディネーターのシークと着地シークの両方を成功シークとして数える。
    private const string SyncSeekLogPattern = @"Timecode sync seek .*success=true";
    private const string LandingSeekLogPattern = @"LTC timecode held: landing seek issued";

    // ---- S: 単発・fps ----

    [SkippableFact(Timeout = 180_000)]
    public void S1_Continue_FollowsLtcAtNormalFrameRate() => Run("S-1", continueMode: true, blackGap: true, scenario =>
    {
        double start = scenario.A.Start + 3;
        scenario.SetSync(true);
        scenario.Play(start, 16);

        // S-1: 判定は「固定目標に一度でも入ったか」ではなく、着地後の 2 秒間の位置系列が
        // そのときの LTC の写像に対して ±0.3 で追従していること。50ms 間隔のラベル読みが
        // 一瞬の窓を逃しても追従そのものを見る（着地が遅れても目標が動くため見逃さない）。
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.TimelineToMedia(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            12, "LTC 追従に入る");

        IReadOnlyList<(double Ltc, double Position)> follow = scenario.SampleFollowWindow(
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50));
        foreach ((double ltc, double position) in follow)
            scenario.Journal.Write("follow-sample", details: new { ltc, position });
        // S-1: LTC はタイムライン秒、位置は素材秒。Continue の写像（開始と MediaIn）で比べる。
        double maxError = LtcFollowSeries.MaxErrorSeconds(
            follow, scenario.A.Start, scenario.A.MediaIn.TotalSeconds);
        scenario.Journal.Write("follow-summary", details: new { samples = follow.Count, maxErrorSeconds = maxError });
        LtcFollowSeries.IsFollowing(follow, scenario.A.Start, scenario.A.MediaIn.TotalSeconds, PositionToleranceSeconds)
            .Should().BeTrue($"着地後の 2 秒間が LTC の写像に対して ±{PositionToleranceSeconds} で追従する（最大誤差 {maxError:F3}s / {follow.Count} サンプル）");

        Thread.Sleep(1500);
        DateTime windowStart = DateTime.Now;
        Thread.Sleep(10_000);

        IReadOnlyList<PerfSegment> segments = scenario.PerfSegmentsSince(windowStart);
        foreach (PerfSegment segment in segments)
            scenario.Journal.Write("fps-segment", details: new { at = segment.At, elapsed = segment.ElapsedSeconds, frameUpdates = segment.FrameUpdates });

        // 期待範囲は素材の fps から作る（2 秒 × fps ± 10%）。30fps 前提の固定値（55〜65）では
        // 60fps の実素材（実測 2 秒で約 120）が落ちる。色素材（30fps）は 54〜66 で従来どおり通る。
        double mediaFps = scenario.MediaFps();
        (int minUpdates, int maxUpdates) = PerfUpdateExpectation.FrameUpdatesRange(mediaFps, 2.0);
        scenario.Journal.Write("fps-expectation", details: new { mediaFps, minUpdates, maxUpdates });

        segments.Should().HaveCountGreaterThanOrEqualTo(3, "10 秒の観測で 2 秒区間が 3 本以上取れる");
        foreach (PerfSegment segment in segments)
            segment.FrameUpdates.Should().BeInRange(minUpdates, maxUpdates,
                $"frameUpdates={segment.FrameUpdates} が {mediaFps:F3}fps 素材の 2 秒区間として正常");
    });

    [SkippableFact(Timeout = 360_000)]
    public void S2_Single_RepeatedLtcJumps_LandWithinTolerance() => Run("S-2", continueMode: false, blackGap: true, scenario =>
    {
        int cycles = scenario.StressCycles(10);
        double low = scenario.A.Start + 3;
        double high = Math.Min(scenario.A.Start + 15, scenario.A.End - 0.5);
        scenario.SetSync(true);
        // D27: 停止モードで回す。保持中は位置が保持値で止まる（ランスルーでは走り続けるのが仕様）。
        scenario.SetSignalLossMode(stop: true);

        int holds = 0;
        int syncSeeksBefore = scenario.CountLogMatches(SyncSeekLogPattern, RegexOptions.IgnoreCase);
        int landingSeeksBefore = scenario.CountLogMatches(LandingSeekLogPattern, RegexOptions.IgnoreCase);
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            foreach (double target in new[] { low, high })
            {
                holds++;
                scenario.CheckHold($"s2-{holds:D2}", target, scenario.A, scenario.A.SingleTarget(target),
                    scenario.Expectation(target == low ? "red (body)" : "red (body)"), holdSeconds: 2.5);
            }
        }

        holds.Should().Be(cycles * 2);
        int syncSeeks = scenario.CountLogMatches(SyncSeekLogPattern, RegexOptions.IgnoreCase) - syncSeeksBefore;
        int landingSeeks = scenario.CountLogMatches(LandingSeekLogPattern, RegexOptions.IgnoreCase) - landingSeeksBefore;
        int successfulSeeks = syncSeeks + landingSeeks;
        scenario.Journal.Write("seek-summary", details: new { holds, successfulSeeks, syncSeeks, landingSeeks });
        successfulSeeks.Should().BeGreaterThanOrEqualTo(cycles * 2, "各保持でシークが成功する（停止モードの着地シークを含む）");
    });

    [SkippableFact(Timeout = 180_000)]
    public void S3_Single_OutOfRangeLtc_StopsAtTrackEnd() => Run("S-3", continueMode: false, blackGap: true, scenario =>
    {
        // Single は LTC をメディア位置へ絶対マップし、製品と同じ [MediaIn, MediaOut] へクランプする（MediaOut 未設定は尺）。範囲外は終端で止まる。
        double outOfRange = scenario.A.End + 15;
        scenario.SetSync(true);
        scenario.Hold(outOfRange, 6);

        double expectedEnd = scenario.A.SingleTarget(outOfRange);
        // 製品の境界ホールド（D33）は ±2 フレームでラッチするため、判定許容も素材 fps 基準の
        // ±2 フレーム + ε にする（60fps 実素材の position=25.033 を通す）。
        double endHoldTolerance = SingleModeClamp.BoundaryHoldTolerance(scenario.MediaFps());
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - expectedEnd) <= endHoldTolerance,
            6, "範囲外 LTC で A の終端に止まる");
        scenario.WaitReference("s3-tail", scenario.A.Symbol, "tail", 2.5, "終端フレームは A の最終フレーム");

        scenario.Hold(scenario.A.Start + 5, 6);
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.SingleTarget(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            6, "LTC を戻すと復帰する");
        scenario.Journal.Write("recovered", details: new { ltc = scenario.LtcSeconds(), position = scenario.Position() });
    });

    [SkippableFact(Timeout = 240_000)]
    public void S4_Single_PlaylistSwitch_ShowsSelectedTrack() => Run("S-4", continueMode: false, blackGap: true, scenario =>
    {
        double ltc = scenario.B.Start + 5;
        scenario.SetSync(true);
        scenario.Hold(ltc, 30);

        scenario.LoadTrack(scenario.B.Index, readyMaxPosition: null);
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.B.SingleTarget(ltc)) <= PositionToleranceSeconds,
            10, $"B ロード後に位置が {scenario.B.SingleTarget(ltc):F3} 付近");
        scenario.WaitTrackPicture("s4-b", scenario.B, 3, "B の絵が出ている（B 以外の参照・黒でない）");

        scenario.LoadTrack(scenario.C.Index, readyMaxPosition: null);
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.C.SingleTarget(ltc)) <= PositionToleranceSeconds,
            10, $"C ロード後に位置が {scenario.C.SingleTarget(ltc):F3} 付近");
        scenario.WaitTrackPicture("s4-c", scenario.C, 3, "C の絵が出ている（C 以外の参照・黒でない）");
    });

    [SkippableFact(Timeout = 180_000)]
    public void S5_Single_OutOfRangeLtc_DoesNotSwitchToOtherTrack() => Run("S-5", continueMode: false, blackGap: true, scenario =>
    {
        // A をアクティブにしたまま LTC を B の範囲へ 5 秒保持しても、B へ切り替わらない。
        scenario.LoadTrack(scenario.A.Index);
        scenario.EnsurePlaying();
        scenario.SetSync(true);
        DateTime holdStartedAt = DateTime.Now;
        scenario.Hold(scenario.B.Start + 5, 6);

        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.SingleTarget(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            6, "A 側の位置のまま");
        scenario.WaitTrackPicture("s5-a", scenario.A, 3, "A の絵のまま（B/C の参照・黒でない）");

        int loadedOther = scenario.CountLogMatchesSince(@"Playlist track loaded index=[12]", holdStartedAt);
        scenario.Journal.Write("switch-observation", details: new { loadedOther, loadedIndex = scenario.LoadedTrackIndex() });
        loadedOther.Should().Be(0, "Single ではアクティブ以外へ切り替わらない");
    });

    // ---- L: 連続追従の詰まり監査（L-1） ----

    /// <summary>
    /// L-1: Single モードで 1 トラック内を連続追従し、窓ごとに frameUpdates・位置の進み・
    /// 誤差を集計して「途中で詰まらないか」を見る。対象トラック・秒数・窓長は
    /// TCS_L1_TRACKS / TCS_L1_FOLLOW_SECONDS / TCS_L1_WINDOW_SECONDS で外から変えられる。
    /// </summary>
    [SkippableFact(Timeout = 900_000)]
    public void L1_Single_ContinuousFollow_DoesNotStall() => Run("L-1", continueMode: false, blackGap: true, scenario =>
    {
        double requestedSeconds = FollowSecondsFromEnvironment();
        double windowSeconds = FollowWindowSecondsFromEnvironment();
        double settlingSeconds = FollowSettlingSecondsFromEnvironment();
        double startGateSeconds = FollowStartGateSecondsFromEnvironment();
        string[] requestedTracks = FollowTracksFromEnvironment();
        scenario.Journal.Write("l1-plan", details: new
        {
            requestedSeconds,
            windowSeconds,
            settlingSeconds,
            startGateSeconds,
            tracks = string.Join(",", requestedTracks),
        });

        scenario.SetSync(true);
        foreach (string token in requestedTracks)
        {
            TrackInfo track = ResolveFollowTrack(scenario, token);
            // 使用尺から先頭 2 秒・末尾 2 秒の余裕を引いた長さに丸める（短すぎれば Skip）。
            double followSeconds = Math.Min(requestedSeconds, track.Used - 4.0);
            Skip.If(followSeconds < 30.0,
                $"L-1 {track.Symbol}: 使用尺 {track.Used:F1}s では連続追従を 30 秒未満（{followSeconds:F1}s）しか回せない");
            scenario.LoadTrack(track.Index);
            scenario.EnsurePlaying();
            RunFollowAudit(scenario, track, followSeconds, windowSeconds, settlingSeconds, startGateSeconds);
        }
    });

    /// <summary>
    /// L-1: 1 トラックの連続追従。追従開始は「誤差が許容内に入るまで待つ（上限 startGateSeconds 秒）」。
    /// 上限に達したら待機時間と最後の誤差を理由に失敗する（追いつけないこと自体が結果）。
    /// 追従に入ってから窓を取り、settlingSeconds 分の先頭窓は判定から除外する（報告には残す）。
    /// 送信は素材の終端手前まで流し、終わったら停止して次のトラックへ持ち越さない。
    /// </summary>
    private static void RunFollowAudit(Scenario scenario, TrackInfo track, double followSeconds, double windowSeconds,
        double settlingSeconds, double startGateSeconds)
    {
        double startLtc = track.MediaIn.TotalSeconds + 2.0;
        // ゲート待ちの間もフレームを流し続ける必要があるため、素材の終端手前まで送る。
        scenario.Play(startLtc, track.Used - startLtc - 1.0);

        DateTime gateStartedAt = DateTime.Now;
        double lastError = double.NaN;
        while (true)
        {
            lastError = Math.Abs(scenario.Position() - track.SingleTarget(scenario.LtcSeconds()));
            if (lastError <= PositionToleranceSeconds)
                break;
            double waited = (DateTime.Now - gateStartedAt).TotalSeconds;
            if (waited >= startGateSeconds)
                throw new TimeoutException(
                    $"{track.Symbol}: 追従開始ゲート {startGateSeconds:F0}s を超えても誤差が許容内に入らない" +
                    $"（待機 {waited:F1}s、最後の誤差 {lastError:F3}s）");
            Thread.Sleep(100);
        }
        scenario.Journal.Write("l1-settle", details: new
        {
            track = track.Symbol,
            startGateSeconds,
            waitedSeconds = Math.Round((DateTime.Now - gateStartedAt).TotalSeconds, 3),
            lastErrorSeconds = JsonNumberOrNull(lastError),
        });

        // ゲート待ちで素材を消費しているため、残りの尺に収まる長さに監査区間を丸める。
        double budgetSeconds = track.MediaOut.TotalSeconds - scenario.LtcSeconds() - 2.0;
        followSeconds = Math.Min(followSeconds, budgetSeconds);
        if (followSeconds < 30.0)
            throw new TimeoutException(
                $"{track.Symbol}: 追従開始後に残る尺が {followSeconds:F1}s しかなく 30 秒の監査を回せない" +
                $"（LTC {scenario.LtcSeconds():F3}s / MediaOut {track.MediaOut.TotalSeconds:F3}s）");

        DateTime startedAt = DateTime.Now;
        var samples = new List<FollowSample>();
        while ((DateTime.Now - startedAt).TotalSeconds < followSeconds)
        {
            double elapsed = (DateTime.Now - startedAt).TotalSeconds;
            samples.Add(new FollowSample(elapsed, scenario.LtcSeconds(), scenario.Position()));
            Thread.Sleep(50);
        }

        // アプリの Playback perf 行（2 秒窓）の最後の 1 本を確定させてから読む。
        Thread.Sleep(2200);
        List<FollowPerfSegment> perf = scenario.PerfSegmentsSince(startedAt)
            .Select(segment => new FollowPerfSegment(
                (segment.At - startedAt).TotalSeconds, segment.ElapsedSeconds, segment.FrameUpdates))
            .Where(segment => segment.AtSeconds <= followSeconds + 2.5)
            .ToList();
        SeekLandingSummary seeks = SeekLandingStats.Summarize(
            scenario.CorrectionSeekSettleSecondsSince(startedAt));
        scenario.Signal.Stop();

        ContinuousFollowSummary summary = ContinuousFollowAudit.Summarize(
            samples, perf, followSeconds, windowSeconds, track.SingleTarget, settlingSeconds);

        foreach (FollowWindow window in summary.Windows)
            scenario.Journal.Write("l1-window", details: new
            {
                track = track.Symbol,
                index = window.Index,
                atSeconds = Math.Round(window.StartSeconds, 3),
                frameUpdates = window.FrameUpdates,
                positionAdvance = Math.Round(window.PositionAdvance, 3),
                ltcAdvance = Math.Round(window.LtcAdvance, 3),
                samples = window.Samples,
                intervalSeconds = Math.Round(window.IntervalSeconds, 3),
                positionVelocity = window.IntervalSeconds > 0
                    ? Math.Round(window.PositionAdvance / window.IntervalSeconds, 3)
                    : 0.0,
                sparse = window.Sparse,
                maxAbsError = JsonNumberOrNull(window.MaxAbsError),
                settling = window.Settling,
            });

        scenario.Journal.Write("l1-seeks", details: new
        {
            track = track.Symbol,
            count = seeks.Count,
            medianSeconds = Math.Round(seeks.MedianSeconds, 3),
            maxSeconds = Math.Round(seeks.MaxSeconds, 3),
        });

        scenario.Journal.Write("l1-summary", details: new
        {
            track = track.Symbol,
            usedSeconds = Math.Round(track.Used, 3),
            followSeconds = Math.Round(followSeconds, 3),
            windowSeconds,
            settlingSeconds,
            windows = summary.Windows.Count,
            auditedWindows = summary.Windows.Count(window => !window.Settling),
            settlingWindows = summary.SettlingWindowCount,
            settlingMaxAbsError = JsonNumberOrNull(summary.SettlingMaxAbsError),
            sparseWindows = summary.SparseWindowCount,
            stallUpdateWindows = summary.StallUpdateWindows,
            stallAdvanceWindows = summary.StallAdvanceWindows,
            maxAbsError = JsonNumberOrNull(summary.MaxAbsError),
            meanFrameUpdates = Math.Round(summary.MeanFrameUpdates, 2),
            expectedFrameUpdates = Math.Round(windowSeconds * MediaFpsForExpectation(scenario, track), 2),
            seekCount = seeks.Count,
            seekMedianSeconds = Math.Round(seeks.MedianSeconds, 3),
            seekMaxSeconds = Math.Round(seeks.MaxSeconds, 3),
            worstUpdateWindow = WindowDetail(summary.WorstUpdates),
            worstAdvanceWindow = WindowDetail(summary.WorstAdvance),
            worstErrorWindow = WindowDetail(summary.WorstError),
        });

        string excluded = $"除外 {summary.SettlingWindowCount} 窓（最大誤差 {summary.SettlingMaxAbsError:F3}s）" +
            $"・疎 {summary.SparseWindowCount} 窓";
        summary.Windows.Count(window => !window.Settling)
            .Should().BeGreaterThan(0, $"{track.Symbol}: 判定対象の窓が 1 つ以上ある（{excluded}）");
        summary.StallUpdateWindows.Should().Be(0,
            $"{track.Symbol}: 判定対象で frameUpdates=0 の窓が無い（{excluded}、最悪 {WindowDetail(summary.WorstUpdates)}）");
        summary.StallAdvanceWindows.Should().Be(0,
            $"{track.Symbol}: 判定対象で再生側の停滞（LTC は窓長の半分以上進み、位置がその半分も進まない）の窓が無い" +
            $"（{excluded}、最悪 {WindowDetail(summary.WorstAdvance)}）");
        summary.MaxAbsError.Should().BeLessThanOrEqualTo(PositionToleranceSeconds,
            $"{track.Symbol}: 判定対象の各窓の最大誤差が ±{PositionToleranceSeconds} 秒以内（{excluded}、最悪 {WindowDetail(summary.WorstError)}）");
    }

    private static string WindowDetail(FollowWindow? window) =>
        window is not { } value
            ? "none"
            : $"index={value.Index} at={value.StartSeconds:F2}s updates={value.FrameUpdates} " +
              $"advance={value.PositionAdvance:F3}s ltcAdvance={value.LtcAdvance:F3}s " +
              $"samples={value.Samples} interval={value.IntervalSeconds:F3}s sparse={value.Sparse} " +
              $"maxError={value.MaxAbsError:F3}s";

    private static double? JsonNumberOrNull(double value) =>
        double.IsFinite(value) ? Math.Round(value, 3) : null;

    /// <summary>
    /// L-1: 期待 frameUpdates に使う素材 fps。プロジェクト記録の frameRate は生成スクリプトが
    /// 書かないことがあるため、画面のメタデータ表示（現在トラックの実 fps）を優先する。
    /// </summary>
    private static double MediaFpsForExpectation(Scenario scenario, TrackInfo track)
    {
        double mediaFps = scenario.MediaFps();
        return mediaFps > 0 ? mediaFps : track.FrameRate;
    }

    private static TrackInfo ResolveFollowTrack(Scenario scenario, string token)
    {
        // A/B/C はプロジェクトのトラック記号に関係なく 1/2/3 本目を指す（実素材でも使える）。
        if (token.Length == 1 && token[0] is >= 'A' and <= 'C')
            return scenario.Tracks[token[0] - 'A'];

        TrackInfo? match = scenario.Tracks.FirstOrDefault(
            candidate => string.Equals(candidate.Symbol, token, StringComparison.OrdinalIgnoreCase));
        Skip.If(match is null, $"TCS_L1_TRACKS の '{token}' がプロジェクトのトラックに無い");
        return match!;
    }

    private static double FollowSecondsFromEnvironment() =>
        ReadPositiveDouble("TCS_L1_FOLLOW_SECONDS", 60.0);

    private static double FollowWindowSecondsFromEnvironment() =>
        ReadPositiveDouble("TCS_L1_WINDOW_SECONDS", 2.0);

    /// <summary>
    /// L-1: 追従開始直後の過渡として判定から除外する長さ。既定 4 秒（2 秒窓 ×2）。
    /// 既存シナリオが着地直後を判定に入れないのに合わせ、除外した窓は報告に残す。
    /// </summary>
    private static double FollowSettlingSecondsFromEnvironment() =>
        ReadPositiveDouble("TCS_L1_SETTLING_SECONDS", 4.0);

    /// <summary>
    /// L-1: 追従開始ゲート（誤差が許容内に入るまでの待ち）の上限。既定 30 秒。
    /// 上限に達したら失敗にする（重い素材で追いつけないこと自体が結果）。
    /// </summary>
    private static double FollowStartGateSecondsFromEnvironment() =>
        ReadPositiveDouble("TCS_L1_START_GATE_SECONDS", 30.0);

    private static string[] FollowTracksFromEnvironment()
    {
        string? raw = Environment.GetEnvironmentVariable("TCS_L1_TRACKS");
        if (string.IsNullOrWhiteSpace(raw))
            return ["A"];
        string[] tokens = raw.Split([',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 ? tokens : ["A"];
    }

    private static double ReadPositiveDouble(string variable, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : fallback;
    }

    // ---- R: 保持 LTC（タイムコード停止）の停止 / ランスルー（D27） ----

    [SkippableFact(Timeout = 240_000)]
    public void R1_StopMode_HeldLtc_PausesAndHoldsPosition() => Run("R-1", continueMode: true, blackGap: true, scenario =>
    {
        const double timeoutSeconds = 0.25; // AppSettings.DefaultLtcSignalLossTimeoutMs
        double start = scenario.A.Start + 3;
        double target = scenario.A.Start + 7;
        double expectedPosition = scenario.A.TimelineToMedia(target);
        scenario.SetSync(true);
        scenario.SetSignalLossMode(stop: true);

        scenario.Play(start, 4.0);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= target - 0.2, 8, "LTC が保持値の手前まで進む");
        scenario.PlayHeld(target, 3.5);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= target - 0.001, 6, "LTC が保持値に到達");
        DateTime holdObservedAt = DateTime.Now;
        scenario.WaitUntil(() => scenario.IsPaused(), timeoutSeconds + scenario.OneFrame + 0.5,
            "保持の検出で一時停止");
        DateTime pausedAt = DateTime.Now;
        double pauseLatencySeconds = (pausedAt - holdObservedAt).TotalSeconds;
        scenario.Journal.Write("hold-pause", details: new
        {
            target,
            expected = expectedPosition,
            pauseLatencySeconds,
            position = scenario.Position(),
            ltc = scenario.LtcSeconds(),
        });

        // タイマー間隔 100ms と UIA 読みの遅れを含めた上限（+1 フレーム + タイマー + 読み）。
        pauseLatencySeconds.Should().BeLessThanOrEqualTo(timeoutSeconds + scenario.OneFrame + 0.35,
            $"{timeoutSeconds:F2}s + 1 フレーム以内の一時停止（観測 {pauseLatencySeconds * 1000.0:F0}ms）");
        scenario.WaitUntil(() => Math.Abs(scenario.Position() - expectedPosition) <= scenario.OneFrame,
            timeoutSeconds + scenario.OneFrame + 1.0, "停止位置が保持値");
        double stoppedPosition = scenario.Position();
        // hold-pause は一時停止を検出した瞬間の値で、保持値への明示着地より前を拾う。
        // 着地後に落ち着いた位置はこちらで見る。
        scenario.Journal.Write("hold-landed", details: new
        {
            target,
            expected = expectedPosition,
            position = stoppedPosition,
            overshoot = stoppedPosition - expectedPosition,
            secondsAfterPause = (DateTime.Now - pausedAt).TotalSeconds,
        });
        Thread.Sleep(1500);
        Math.Abs(scenario.Position() - stoppedPosition).Should().BeLessThanOrEqualTo(scenario.OneFrame,
            "保持中は停止位置が動かない");
        scenario.WaitTrackPicture("r1-hold", scenario.A, 2, "保持中は A の本文");
    });

    [SkippableFact(Timeout = 240_000)]
    public void R2_StopMode_HeldThenResent_ResumesAndFollows() => Run("R-2", continueMode: true, blackGap: true, scenario =>
    {
        const double timeoutSeconds = 0.25;
        double start = scenario.A.Start + 3;
        double target = scenario.A.Start + 7;
        double expectedPosition = scenario.A.TimelineToMedia(target);
        scenario.SetSync(true);
        scenario.SetSignalLossMode(stop: true);

        scenario.Play(start, 4.0);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= target - 0.2, 8, "LTC が保持値の手前まで進む");
        scenario.PlayHeld(target, 3.0);

        // LTC が保持値へ届いた時刻を一時停止の基準にする（R-1 と同じ基準）。ここでは判定しない
        // ので、届かないまま時間切れになっても後続の待ちと判定はこれまでどおり動く。
        DateTime holdObservedAt = DateTime.Now;
        DateTime ltcDeadline = holdObservedAt.AddSeconds(6);
        while (scenario.LtcSeconds() < target - 0.001 && DateTime.Now < ltcDeadline)
        {
            Thread.Sleep(50);
            holdObservedAt = DateTime.Now;
        }

        scenario.WaitUntil(() => scenario.IsPaused(), timeoutSeconds + scenario.OneFrame + 0.5,
            "保持の検出で一時停止");
        DateTime pausedAt = DateTime.Now;
        scenario.Journal.Write("hold-pause", details: new
        {
            target,
            expected = expectedPosition,
            pauseLatencySeconds = (pausedAt - holdObservedAt).TotalSeconds,
            position = scenario.Position(),
            ltc = scenario.LtcSeconds(),
        });

        // 保持値への明示着地が済むまで、R-1 の判定と同じ幅（1 フレーム）と同じ時間だけ様子を見る。
        // ここでは判定しないので、着地しないまま時間切れになっても送出の再開へ進む。
        DateTime landingDeadline = pausedAt.AddSeconds(timeoutSeconds + scenario.OneFrame + 1.0);
        while (Math.Abs(scenario.Position() - expectedPosition) > scenario.OneFrame
            && DateTime.Now < landingDeadline)
        {
            Thread.Sleep(50);
        }
        double landedPosition = scenario.Position();
        scenario.Journal.Write("hold-landed", details: new
        {
            target,
            expected = expectedPosition,
            position = landedPosition,
            overshoot = landedPosition - expectedPosition,
            secondsAfterPause = (DateTime.Now - pausedAt).TotalSeconds,
            landed = Math.Abs(landedPosition - expectedPosition) <= scenario.OneFrame,
        });

        scenario.Play(target, 8.0);
        scenario.WaitUntil(() => !scenario.IsPaused(), 4, "送出再開で再生が復帰");
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.TimelineToMedia(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            8, "復帰後は LTC に追従");
    });

    [SkippableFact(Timeout = 240_000)]
    public void R3_RunThrough_HeldLtc_KeepsPlaying() => Run("R-3", continueMode: true, blackGap: true, scenario =>
    {
        double start = scenario.A.Start + 3;
        double target = scenario.A.Start + 7;
        scenario.SetSync(true);
        scenario.SetSignalLossMode(stop: false);

        scenario.Play(start, 4.0);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= target - 0.2, 8, "LTC が保持値の手前まで進む");
        scenario.PlayHeld(target, 4.0);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= target - 0.001, 6, "LTC が保持値に到達");
        const double observeSeconds = 3.0;
        double before = scenario.Position();
        Thread.Sleep((int)(observeSeconds * 1000));
        double after = scenario.Position();
        double advanced = after - before;
        // target は LTC の保持値（タイムライン秒）、advancedSeconds は observeSeconds の間に
        // 進んだ再生位置の差。両者は別の量なので、名前で区別できるようにする。
        scenario.Journal.Write("run-through-hold", details: new
        {
            holdTarget = target,
            observeSeconds,
            positionBefore = before,
            positionAfter = after,
            advancedSeconds = advanced,
        });
        advanced.Should().BeGreaterThanOrEqualTo(2.5, "ランスルーは保持中も走り続ける");
        scenario.IsPaused().Should().BeFalse();
    });

    [SkippableFact(Timeout = 240_000)]
    public void R4_RunThrough_HeldThenResent_Resyncs() => Run("R-4", continueMode: true, blackGap: true, scenario =>
    {
        double start = scenario.A.Start + 3;
        double target = scenario.A.Start + 7;
        scenario.SetSync(true);
        scenario.SetSignalLossMode(stop: false);

        scenario.Play(start, 4.0);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= target - 0.2, 8, "LTC が保持値の手前まで進む");
        scenario.PlayHeld(target, 3.0);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= target - 0.001, 6, "LTC が保持値に到達");
        Thread.Sleep(2000);

        scenario.Play(target, 8.0);
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.TimelineToMedia(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            8, "再送出で LTC へ再同期（ジャンプで戻る）");
        scenario.Journal.Write("run-through-resync", details: new
        {
            ltc = scenario.LtcSeconds(),
            position = scenario.Position(),
        });
    });

    // ---- C: Continue のジャンプ ----

    [SkippableFact(Timeout = 360_000)]
    public void C1_Continue_RepeatedJumpsWithinOneTrack_LandWithinTolerance() => Run("C-1", continueMode: true, blackGap: true, scenario =>
    {
        int cycles = scenario.StressCycles(10);
        double low = scenario.A.Start + 3;
        double high = Math.Min(scenario.A.Start + 15, scenario.A.End - 0.5);
        scenario.SetSync(true);

        int holds = 0;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            foreach (double target in new[] { low, high })
            {
                holds++;
                scenario.CheckHold($"c1-{holds:D2}", target, scenario.A,
                    scenario.A.TimelineToMedia(target), scenario.Expectation("red (body)"), holdSeconds: 2.5,
                    sampleBlackDuringJump: true);
            }
        }

        holds.Should().Be(cycles * 2);
    });

    [SkippableFact(Timeout = 420_000)]
    public void C2_Continue_RepeatedJumpsAcrossTracks_LandWithinTolerance() => Run("C-2", continueMode: true, blackGap: true, scenario =>
    {
        int cycles = scenario.StressCycles(10);
        double inA = scenario.A.Start + 7;
        double inB = scenario.B.Start + 10;
        scenario.SetSync(true);

        int holds = 0;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            holds++;
            scenario.CheckHold($"c2-{holds:D2}", inA, scenario.A,
                scenario.A.TimelineToMedia(inA), scenario.Expectation("red (body)"), holdSeconds: 2.5,
                sampleBlackDuringJump: true);
            holds++;
            scenario.CheckHold($"c2-{holds:D2}", inB, scenario.B,
                scenario.B.TimelineToMedia(inB), scenario.Expectation("green (body)"), holdSeconds: 3.5,
                sampleBlackDuringJump: true);
        }

        holds.Should().Be(cycles * 2);
    });

    // ---- G: Continue + Black のギャップ ----

    [SkippableFact(Timeout = 180_000)]
    public void G1_ContinueBlack_LtcCrossesTrackEnd_ShowsBlack() => Run("G-1", continueMode: true, blackGap: true, scenario =>
    {
        scenario.SetSync(true);
        scenario.Play(scenario.A.End - 2, 10);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.A.End - 0.05, 8, "LTC が A の終端を通過");
        scenario.WaitBlack("g1-black", 1.0, "終端通過後 1 秒以内に黒");
    });

    [SkippableFact(Timeout = 180_000)]
    public void G2_ContinueBlack_LtcEntersNextTrack_LeavesBlack() => Run("G-2", continueMode: true, blackGap: true, scenario =>
    {
        scenario.SetSync(true);
        scenario.Play(scenario.A.End - 2, 14);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.B.Start - 0.05, 8, "LTC が B の先頭を通過");
        // 冒頭 1 秒は B の先頭フレーム（色素材ではマゼンタ）。参照一致するなら B の参照であること。
        // 参照が黒の素材では「黒」と「head で静止」を区別できないため非黒を要求しない。
        bool requireNotBlack = scenario.HeadReferenceNotBlack(scenario.B);
        bool ambiguousHead = scenario.SkipAmbiguousReference("g2-enter", scenario.B.Symbol, "head");
        scenario.Journal.Write("black-judgment", details: new { name = "g2-enter", symbol = scenario.B.Symbol, requireNotBlack, ambiguousHead });
        scenario.WaitForFrame("g2-enter", TimeSpan.FromSeconds(1.0),
            (signature, match) => ambiguousHead || ((!requireNotBlack || !signature.IsBlack) &&
                                  (!match.IsMatch || match.MatchesTrack(scenario.B.Symbol))),
            "1 秒以内に黒から B の絵へ");

        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.B.Start + 1.5, 3, "B の冒頭を通過");
        double before = scenario.Position();
        scenario.WaitUntil(() => scenario.Position() > before + 0.15, 3, "B の再生が進む");
        scenario.WaitTrackPicture("g2-play", scenario.B, 2, "B の再生中");
    });

    [SkippableFact(Timeout = 180_000)]
    public void G3_ContinueBlack_JumpIntoGap_ShowsBlack() => Run("G-3", continueMode: true, blackGap: true, scenario =>
    {
        scenario.SetSync(true);
        scenario.Play(scenario.A.Start + 3, 10);
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.TimelineToMedia(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            8, "A の再生中");

        scenario.Hold(scenario.A.End + 2, 6);
        scenario.WaitBlack("g3-black", 1.5, "ジャンプでギャップへ入ったら黒");
    });

    [SkippableFact(Timeout = 180_000)]
    public void G4_ContinueBlack_JumpFromGapIntoNextTrack_Recovers() => Run("G-4", continueMode: true, blackGap: true, scenario =>
    {
        scenario.SetSync(true);
        scenario.Hold(scenario.A.End + 2, 6);
        scenario.WaitBlack("g4-black", 1.5, "先にギャップの黒を確認");

        scenario.Hold(scenario.B.Start + 10, 8);
        DateTime observed = DateTime.Now;
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.B.TimelineToMedia(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            6, "B の途中の位置へ復帰");
        scenario.WaitTrackPicture("g4-play", scenario.B, 3, "B の絵へ復帰（黒でない）");
        scenario.Journal.Write("gap-exit", details: new { elapsedMs = (DateTime.Now - observed).TotalMilliseconds });
    });

    [SkippableFact(Timeout = 600_000)]
    public void G5_ContinueBlack_JumpCycleAroundTracksAndGaps_KeepsExpectedPictures() => Run("G-5", continueMode: true, blackGap: true, scenario =>
    {
        int cycles = scenario.StressCycles(5);
        scenario.SetSync(true);

        int holds = 0;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            holds++;
            scenario.CheckHold($"g5-{holds:D2}", scenario.A.Start + 7, scenario.A,
                scenario.A.TimelineToMedia(scenario.A.Start + 7), scenario.Expectation("red (body)"), holdSeconds: 3.0);
            holds++;
            scenario.CheckGap($"g5-{holds:D2}", scenario.A.End + 2);
            holds++;
            scenario.CheckHold($"g5-{holds:D2}", scenario.B.Start + 10, scenario.B,
                scenario.B.TimelineToMedia(scenario.B.Start + 10), scenario.Expectation("green (body)"), holdSeconds: 3.0);
            holds++;
            scenario.CheckGap($"g5-{holds:D2}", scenario.B.End + 2);
            holds++;
            scenario.CheckHold($"g5-{holds:D2}", scenario.C.Start + 5, scenario.C,
                scenario.C.TimelineToMedia(scenario.C.Start + 5), scenario.Expectation("blue (body)"), holdSeconds: 3.0);
        }

        holds++;
        scenario.CheckHold($"g5-{holds:D2}", scenario.A.Start + 7, scenario.A,
            scenario.A.TimelineToMedia(scenario.A.Start + 7), scenario.Expectation("red (body)"), holdSeconds: 3.0);
        scenario.Journal.Write("cycle-summary", details: new { cycles, holds });
    });

    [SkippableFact(Timeout = 180_000)]
    public void G6_ContinueBlack_LtcBeforeFirstTrack_ShowsBlack() => Run("G-6", continueMode: true, blackGap: true, scenario =>
    {
        scenario.SetSync(true);
        scenario.Play(1, 10);
        scenario.WaitUntil(() => scenario.LtcSeconds() is >= 1.5 and <= 3.5, 8, "先頭オフセット内の LTC");
        scenario.WaitBlack("g6-black", 2.0, "先頭オフセット領域は黒");

        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.A.Start + 0.3, 4, "A の先頭を通過");
        // 参照 head が黒の素材では黒と head の区別ができないため、非黒を要求せず参照一致だけで見る。
        bool requireNotBlack = scenario.HeadReferenceNotBlack(scenario.A);
        bool ambiguousHead = scenario.SkipAmbiguousReference("g6-enter", scenario.A.Symbol, "head");
        scenario.Journal.Write("black-judgment", details: new { name = "g6-enter", symbol = scenario.A.Symbol, requireNotBlack, ambiguousHead });
        scenario.WaitForFrame("g6-enter", TimeSpan.FromSeconds(2.0),
            (signature, match) => ambiguousHead || ((!requireNotBlack || !signature.IsBlack) &&
                                  (!match.IsMatch || match.MatchesTrack(scenario.A.Symbol))),
            "A の先頭フレームで黒から復帰");

        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.A.Start + 1.5, 4, "A の冒頭を通過");
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.TimelineToMedia(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            4, "A の再生位置が追従");
    });

    // ---- F: Continue + Freeze のギャップ ----

    [SkippableFact(Timeout = 180_000)]
    public void F1_ContinueFreeze_LtcCrossesTrackEnd_HoldsLastFrame() => Run("F-1", continueMode: true, blackGap: false, scenario =>
    {
        scenario.SetSync(true);
        scenario.Play(scenario.A.End - 2, 10);
        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.A.End - 0.05, 8, "LTC が A の終端を通過");
        scenario.WaitReference("f1-tail", scenario.A.Symbol, "tail", 2.0, "終端通過後は A の最終フレーム");

        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.A.End + 3.5, 6, "ギャップの後半まで保持");
        scenario.WaitReference("f1-tail-hold", scenario.A.Symbol, "tail", 2.0, "ギャップの間は A の最終フレームのまま");
    });

    [SkippableFact(Timeout = 180_000)]
    public void F2_ContinueFreeze_JumpIntoGap_HoldsLastFrame() => Run("F-2", continueMode: true, blackGap: false, scenario =>
    {
        scenario.SetSync(true);
        scenario.Play(scenario.A.Start + 3, 10);
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.TimelineToMedia(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            8, "A の再生中");

        scenario.Hold(scenario.A.End + 2, 6);
        scenario.WaitReference("f2-tail", scenario.A.Symbol, "tail", 2.0, "ジャンプでギャップへ入ったら A の最終フレーム");
    });

    [SkippableFact(Timeout = 240_000)]
    public void F3_ContinueFreeze_JumpBetweenGaps_UpdatesHeldFrame() => Run("F-3", continueMode: true, blackGap: false, scenario =>
    {
        scenario.SetSync(true);
        scenario.Hold(scenario.A.End + 2, 8);
        scenario.WaitReference("f3-tail-a", scenario.A.Symbol, "tail", 2.5, "A の後のギャップは A の最終フレーム");

        scenario.Hold(scenario.B.End + 2, 10);
        scenario.WaitReference("f3-tail-b", scenario.B.Symbol, "tail", 4.0, "B の後のギャップは B の最終フレームへ更新");
    });

    [SkippableFact(Timeout = 600_000)]
    public void F4_ContinueFreeze_JumpCycleAroundTracksAndGaps_KeepsExpectedPictures() => Run("F-4", continueMode: true, blackGap: false, scenario =>
    {
        int cycles = scenario.StressCycles(5);
        scenario.SetSync(true);

        int holds = 0;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            holds++;
            scenario.CheckHold($"f4-{holds:D2}", scenario.A.Start + 7, scenario.A,
                scenario.A.TimelineToMedia(scenario.A.Start + 7), scenario.Expectation("red (body)"), holdSeconds: 3.0);
            holds++;
            scenario.CheckFreeze($"f4-{holds:D2}", scenario.A.End + 2, scenario.A);
            holds++;
            scenario.CheckHold($"f4-{holds:D2}", scenario.B.Start + 10, scenario.B,
                scenario.B.TimelineToMedia(scenario.B.Start + 10), scenario.Expectation("green (body)"), holdSeconds: 3.0);
            holds++;
            scenario.CheckFreeze($"f4-{holds:D2}", scenario.B.End + 2, scenario.B);
            holds++;
            scenario.CheckHold($"f4-{holds:D2}", scenario.C.Start + 5, scenario.C,
                scenario.C.TimelineToMedia(scenario.C.Start + 5), scenario.Expectation("blue (body)"), holdSeconds: 3.0);
        }

        holds++;
        scenario.CheckHold($"f4-{holds:D2}", scenario.A.Start + 7, scenario.A,
            scenario.A.TimelineToMedia(scenario.A.Start + 7), scenario.Expectation("red (body)"), holdSeconds: 3.0);
        scenario.Journal.Write("cycle-summary", details: new { cycles, holds });
    });

    [SkippableFact(Timeout = 180_000)]
    public void F5_ContinueFreeze_LtcBeforeFirstTrack_HoldsFirstFrame() => Run("F-5", continueMode: true, blackGap: false, scenario =>
    {
        scenario.SetSync(true);
        scenario.Play(1, 10);
        scenario.WaitUntil(() => scenario.LtcSeconds() is >= 1.5 and <= 3.5, 8, "先頭オフセット内の LTC");
        scenario.WaitReference("f5-head", scenario.A.Symbol, "head", 2.5, "先頭オフセット領域は A の冒頭フレーム");

        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.A.Start + 0.3, 4, "A の先頭を通過");
        scenario.WaitReference("f5-head-enter", scenario.A.Symbol, "head", 2.5, "再生開始直後も A の冒頭フレーム");

        scenario.WaitUntil(() => scenario.LtcSeconds() >= scenario.A.Start + 1.5, 4, "A の冒頭を通過");
        scenario.WaitUntil(
            () => Math.Abs(scenario.Position() - scenario.A.TimelineToMedia(scenario.LtcSeconds())) <= PositionToleranceSeconds,
            4, "A の再生位置が追従");
        scenario.Journal.Write("after-head", details: new { ltc = scenario.LtcSeconds(), position = scenario.Position() });
    });

    // ---- harness ----

    private static void Run(string testId, bool continueMode, bool blackGap, Action<Scenario> body)
    {
        using var scenario = Scenario.Start(testId, continueMode, blackGap);
        try
        {
            body(scenario);
            scenario.VerifyAndExit();
        }
        catch (Exception error)
        {
            scenario.Journal.Write("failure", details: new { error = error.ToString() });
            throw;
        }
    }

    private sealed record TrackInfo(
        int Index, string Symbol, TimeSpan TimelineOffset, TimeSpan MediaIn, TimeSpan MediaOut,
        TimeSpan Duration, double FrameRate)
    {
        public double Start => TimelineOffset.TotalSeconds;
        public double End => Start + (MediaOut - MediaIn).TotalSeconds;
        public double Used => (MediaOut - MediaIn).TotalSeconds;

        /// <summary>Continue: タイムライン秒 → メディア位置。</summary>
        public double TimelineToMedia(double ltcSeconds) => ltcSeconds - Start + MediaIn.TotalSeconds;

        /// <summary>Single: LTC 秒はそのままメディア位置（製品と同じ [MediaIn, MediaOut] へクランプ）。</summary>
        public double SingleTarget(double ltcSeconds) =>
            SingleModeClamp.Target(ltcSeconds, MediaIn.TotalSeconds, MediaOut.TotalSeconds);
    }

    private sealed record PerfSegment(DateTime At, double ElapsedSeconds, int FrameUpdates);

    private sealed class Scenario : IDisposable
    {
        private readonly string _exePath;
        private readonly DateTime _startedAt;
        private bool _exited;

        private Scenario(
            string exePath, string repoRoot, string reportDir, bool isDefaultProject,
            ProjectData project, IReadOnlyList<TrackInfo> tracks, DateTime startedAt)
        {
            _exePath = exePath;
            ReportDir = reportDir;
            IsDefaultProject = isDefaultProject;
            Tracks = tracks;
            Journal = new MonkeyJournal(Path.Combine(reportDir, "harness.jsonl"), 0);
            _startedAt = startedAt;
        }

        public E2EAppRunner App { get; private set; } = null!;
        public LtcSignalPlayer Signal { get; private set; } = null!;
        public MonkeyJournal Journal { get; }
        public string ReportDir { get; }
        public bool IsDefaultProject { get; }
        public IReadOnlyList<TrackInfo> Tracks { get; }
        public ReferenceSet References { get; } = new();
        public TrackInfo A => Tracks[0];
        public TrackInfo B => Tracks[1];
        public TrackInfo C => Tracks[2];
        public double OneFrame => 1.0 / (Tracks[0].FrameRate > 0 ? Tracks[0].FrameRate : 30.0);

        /// <summary>信号断モードが停止（SetSignalLossMode(true)）か。既定のコンボ index 0 はランスルー。</summary>
        public bool SignalLossStop { get; private set; }

        public static Scenario Start(string testId, bool continueMode, bool blackGap)
        {
            (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
            Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");
            string repoRoot = FindRepoRoot();
            string mediaDir = Path.Combine(repoRoot, "artifacts", "media");

            bool isDefaultProject = string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(ProjectVariable));
            string projectPath;
            if (isDefaultProject)
            {
                string[] mediaNames = ["ltc_a.mp4", "ltc_b.mp4", "ltc_c.mp4"];
                Skip.If(mediaNames.Any(name => !File.Exists(Path.Combine(mediaDir, name))),
                    "色素材が無い（scripts/make-e2e-media.ps1）");
                string fixture = Path.Combine(repoRoot, "tests", "TimecodeSyncPlayer.Tests",
                    "Fixtures", "ltc-scenario.tsp");
                Skip.If(!File.Exists(fixture), "Fixtures/ltc-scenario.tsp が無い");
                projectPath = Path.Combine(mediaDir, "ltc-scenario.tsp");
                File.Copy(fixture, projectPath, overwrite: true);
            }
            else
            {
                projectPath = Environment.GetEnvironmentVariable(ProjectVariable)!;
                Skip.If(!File.Exists(projectPath), $"{ProjectVariable} の .tsp が無い");
            }

            ProjectData? project = ProjectSerializer.LoadAsync(projectPath).GetAwaiter().GetResult();
            Skip.If(project is null, "プロジェクトを読み込めない");
            List<TrackInfo> tracks = BuildTracks(project!, isDefaultProject);
            Skip.If(tracks.Count < 3, "検証には有効トラックが 3 本必要");

            Skip.If(LtcSignalPlayer.FindCableCaptureDeviceName() is null,
                "有効な VB-CABLE 録音デバイス（CABLE Output）が見つかりません。");
            bool cableOk = LtcSignalPlayer.TryCreateCablePlayer(out LtcSignalPlayer? signal, out string? cableReason);
            Skip.If(!cableOk || signal is null, cableReason ?? "CABLE Input を利用できません。");

            string? reportBase = Environment.GetEnvironmentVariable(ReportVariable);
            string baseDir = string.IsNullOrWhiteSpace(reportBase)
                ? Path.Combine(repoRoot, "artifacts", "ltc-scenarios")
                : Path.GetFullPath(reportBase);
            string reportDir = Path.Combine(baseDir, $"{testId}-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(reportDir);

            var scenario = new Scenario(exePath, repoRoot, reportDir, isDefaultProject, project!, tracks, DateTime.Now);
            try
            {
                scenario.Signal = signal!;
                scenario.Journal.Write("scenario-start", details: new
                {
                    testId, continueMode, blackGap, isDefaultProject,
                    tracks = tracks.Select(t => new { t.Symbol, t.Start, t.End, t.Used, t.MediaIn, t.MediaOut, t.Duration, t.FrameRate }),
                });
                scenario.StartApp(projectPath);
                scenario.ConfigureLtc();
                scenario.CaptureReferences();
                scenario.PrepareForTest(continueMode, blackGap);
                return scenario;
            }
            catch
            {
                scenario.Dispose();
                throw;
            }
        }

        private static List<TrackInfo> BuildTracks(ProjectData project, bool isDefaultProject)
        {
            var tracks = new List<TrackInfo>();
            foreach (TrackData track in project.Tracks.Where(t => t.IsEnabled))
            {
                int index = tracks.Count;
                // A generated project names each track by its folder symbol (M1..M7,
                // see scripts/make-ltc-scenario-project.ps1 -Media); use it so journals
                // and reference images say which file of the folder was involved.
                string symbol = isDefaultProject
                    ? index < 3 ? ((char)('A' + index)).ToString() : $"M{index + 1}"
                    : IsMediaSymbol(track.Name) ? track.Name!.ToUpperInvariant() : $"M{index + 1}";
                TimeSpan mediaOut = track.MediaOut ?? track.MediaDuration;
                tracks.Add(new TrackInfo(index, symbol, track.TimelineOffset, track.MediaIn, mediaOut,
                    track.MediaDuration, track.FrameRate ?? 30.0));
            }

            return tracks;
        }

        private static bool IsMediaSymbol(string? name) =>
            name is not null && Regex.IsMatch(name, @"^[Mm][1-9][0-9]*$");

        private void StartApp(string projectPath)
        {
            // 検証機では既定の再生デバイスが LTC ループと同じ CABLE Input になり得る。
            // 実素材の音声トラックに LTC が入っていると（制作マスターでは一般的）、アプリの
            // 再生音が LTC 入力へ回り込み、テストの送った LTC に別の LTC が混ざる。
            // シナリオは音を使わないので、設定が未作成ならミュートで起動する。
            string settingsPath = Path.Combine(ReportDir, "settings.json");
            if (!File.Exists(settingsPath))
                File.WriteAllText(settingsPath, "{\n  \"isMuted\": true\n}\n");

            App = E2EAppRunner.Start(_exePath, $"--load-project \"{projectPath}\"",
                settingsPath, pausePlaybackIfNeeded: false);
            MonkeyJson.WriteAppProcessMarker(Path.Combine(ReportDir, "app-process.json"), App.Process);

            DateTime? appStartedAt = null;
            try { appStartedAt = App.Process.StartTime; } catch (InvalidOperationException) { }
            Journal.Write("app-started", details: new { appStartedAt });

            WaitUntil(() => LoadedTrackIndex() >= 0, 20, "プロジェクトの初回ロード");
        }

        private void ConfigureLtc()
        {
            App.Button("BtnRefreshLtcDevices").Invoke();
            ComboBox devices = App.Combo("LtcDeviceCombo");
            int index = -1;
            WaitUntil(() =>
            {
                index = Array.FindIndex(devices.Items,
                    item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
                return index >= 0;
            }, 8, "CABLE Output の列挙");
            devices.Select(index);
            App.Combo("LtcFpsModeCombo").Select(2);
            WaitUntil(() => App.Combo("LtcFpsModeCombo").SelectedItem?.Name.Contains("25", StringComparison.Ordinal) == true,
                3, "LTC 25fps 固定");
            App.Combo("LtcSignalLossModeCombo").Select(0);
        }

        /// <summary>
        /// 参照フレーム: 各トラックを読み込み、一時停止で MediaIn と MediaOut-1 フレームへ
        /// シークして画面を読み戻す（2.5 節）。同期 OFF・LTC 送信前に行う。
        /// シーク後は位置が目標 ±1 フレームに入るまで最大 6 秒待つ（絵の変化は早期退出の条件で、
        /// 前の採取と同じ絵でも位置が入れば採用する）。3 秒経っても位置が動かなければ同じ目標へ
        /// 1 回だけ再シークする。
        /// </summary>
        private void CaptureReferences()
        {
            SetSync(false);
            FrameSignature? previous = null;
            for (int i = 0; i < 3; i++)
            {
                TrackInfo track = Tracks[i];
                LoadTrack(track.Index);
                Pause();
                Seek(track.MediaIn.TotalSeconds);
                FrameSignature head = CaptureReferenceAfterSeek(track, "head", track.MediaIn.TotalSeconds, previous);
                References.Add(track.Symbol, "head", $"ref_{track.Symbol}_head", head);

                double tail = Math.Max(0, track.MediaOut.TotalSeconds - OneFrame);
                Seek(tail);
                FrameSignature tailSignature = CaptureReferenceAfterSeek(track, "tail", tail, head);
                References.Add(track.Symbol, "tail", $"ref_{track.Symbol}_tail", tailSignature);
                Journal.Write("reference-captured", details: new
                {
                    symbol = track.Symbol,
                    tailTarget = tail,
                    tailObserved = Position(),
                });
                previous = tailSignature;
            }
        }

        /// <summary>
        /// シーク後の参照採取。位置が目標 ±1 フレームに入ったら完了（必須）。絵の変化は待ちを早く
        /// 抜けるだけの条件で、前の採取と同じ絵でも位置が入れば採用し reference-same を残す
        /// （現場素材の黒フェードアウト→フェードインのように正当に同じ絵になる場合がある）。
        /// 6 秒待っても位置が目標に入らないときだけ失敗する（4K の CPU デコードでは shim の
        /// 一時停止シークのポンプ予算 4 秒を越えることがあるため、3 秒では足りない）。
        /// </summary>
        private FrameSignature CaptureReferenceAfterSeek(
            TrackInfo track, string kind, double target, FrameSignature? previous)
        {
            // 許容幅は「いま読み込んでいる素材の 1 フレーム」。OneFrame は先頭トラックの
            // フレームレート基準なので、24fps の素材を 60fps 基準（0.0167 秒）で見てしまい、
            // 目標 +1 フレーム（0.0417 秒）で止まった位置が永久に「未到達」になっていた。
            double frameSeconds = CurrentFrameSeconds();
            const double waitSeconds = 6.0;
            const double reseekAfterSeconds = 3.0;
            DateTime startedAt = DateTime.UtcNow;
            DateTime deadline = startedAt + TimeSpan.FromSeconds(waitSeconds);
            bool reseeked = false;
            string imageName = $"ref_{track.Symbol}_{kind}";
            for (int attempt = 1; ; attempt++)
            {
                FrameSignature signature = LtcScenarioFrameProbe.Capture(App, ReportDir, imageName, Journal);
                double observed = Position();
                bool sameAsPrevious = previous is FrameSignature prev && prev.IsSameFrameAs(signature);
                if (ReferenceCaptureReadiness.IsReady(observed, target, frameSeconds))
                {
                    if (sameAsPrevious)
                        Journal.Write("reference-same", details: new
                        {
                            symbol = track.Symbol,
                            kind,
                            attempt,
                            target = Math.Round(target, 3),
                            observed = JsonNumber(observed),
                            note = "絵は前の採取と同じだが位置が目標 ±1 フレームに入ったため採用",
                        });
                    return signature;
                }

                Journal.Write("reference-stale", details: new
                {
                    symbol = track.Symbol,
                    kind,
                    attempt,
                    position = JsonNumber(observed),
                    target = Math.Round(target, 3),
                    frameSeconds = JsonNumber(frameSeconds),
                    sameAsPrevious,
                    nearestKnownColor = LtcScenarioFrameProbe.DescribeNearestKnownColor(signature),
                });
                if (DateTime.UtcNow >= deadline)
                {
                    Journal.Write("reference-recapture-failed", details: new
                    {
                        symbol = track.Symbol,
                        kind,
                        attempts = attempt,
                        reseeked,
                    });
                    throw new TimeoutException(
                        $"参照 {imageName} の位置が目標 {target:F3} ±1 フレームに入らない" +
                        $"（{waitSeconds:F0} 秒待ってもシーク位置に到達しない, 再シーク={reseeked}）");
                }

                if (!reseeked && (DateTime.UtcNow - startedAt).TotalSeconds >= reseekAfterSeconds)
                {
                    reseeked = true;
                    Journal.Write("reference-reseek", details: new
                    {
                        symbol = track.Symbol,
                        kind,
                        attempt,
                        target = Math.Round(target, 3),
                        observed = JsonNumber(observed),
                    });
                    Seek(target);
                    continue;
                }

                Thread.Sleep(200);
            }
        }

        private void PrepareForTest(bool continueMode, bool blackGap)
        {
            LoadTrack(A.Index);
            EnsurePlaying();
            SetSyncMode(continueMode);
            if (continueMode) SetGapBehavior(blackGap);
            if (App.Button("BtnStartLtc").IsEnabled) App.Button("BtnStartLtc").Invoke();
            Journal.Write("test-ready", details: new { continueMode, blackGap, loadedIndex = LoadedTrackIndex() });
        }

        // ---- LTC ----

        public void Play(double startSeconds, double durationSeconds) =>
            Signal.Play(ToTimecode(startSeconds), LtcFps, TimeSpan.FromSeconds(durationSeconds));

        public void Hold(double targetSeconds, double durationSeconds = 2.5)
        {
            Signal.PlayHeld(targetSeconds, LtcFps, TimeSpan.FromSeconds(Math.Max(2.5, durationSeconds)));
            WaitUntil(() => Math.Abs(LtcSeconds() - targetSeconds) <= 0.05, 6, $"保持 LTC {targetSeconds:F2} の受信");
            Journal.Write("hold", details: new { target = targetSeconds, observed = LtcSeconds() });
        }

        /// <summary>D27: 保持 LTC を送る。受信待ちは呼び出し側が行う（停止の観測を先に始めるため）。</summary>
        public void PlayHeld(double targetSeconds, double durationSeconds) =>
            Signal.PlayHeld(targetSeconds, LtcFps, TimeSpan.FromSeconds(Math.Max(1.0, durationSeconds)));

        public bool IsPaused() => App.Button("BtnPlay").Name == "▶";

        private static LtcTimecode ToTimecode(double seconds)
        {
            int frame = (int)Math.Round(seconds * LtcFps);
            return new LtcTimecode(
                frame / (LtcFps * 3600), frame / (LtcFps * 60) % 60, frame / LtcFps % 60, frame % LtcFps, false);
        }

        // ---- UI ----

        public void SetSync(bool enabled)
        {
            Button button = App.Button("BtnToggleSync");
            if (button.Name.Contains("ON", StringComparison.OrdinalIgnoreCase) != enabled)
                button.Invoke();
            string expected = enabled ? "ON" : "OFF";
            WaitUntil(() => App.Button("BtnToggleSync").Name.Contains(expected, StringComparison.OrdinalIgnoreCase),
                5, $"同期 {expected}");
        }

        private void SetSyncMode(bool continueMode)
        {
            ComboBox combo = App.Combo("SyncModeCombo");
            combo.Select(continueMode ? 1 : 0);
            string expected = continueMode ? "Continue" : "Single";
            WaitUntil(() => combo.SelectedItem?.Name.Contains(expected, StringComparison.Ordinal) == true,
                5, $"同期モード {expected}");
        }

        private void SetGapBehavior(bool black)
        {
            ComboBox combo = App.Combo("GapBehaviorCombo");
            WaitUntil(() => combo.IsEnabled, 3, "ギャップ動作の選択可");
            combo.Select(black ? 0 : 1);
            string expected = black ? "Black" : "Freeze";
            WaitUntil(() => combo.SelectedItem?.Name.Contains(expected, StringComparison.Ordinal) == true,
                5, $"ギャップ動作 {expected}");
        }

        /// <summary>D27: 信号断時の動作を明示する（既定はランスルー）。</summary>
        public void SetSignalLossMode(bool stop)
        {
            ComboBox combo = App.Combo("LtcSignalLossModeCombo");
            combo.Select(stop ? 1 : 0);
            string expected = stop ? "停止" : "ランスルー";
            WaitUntil(() => combo.SelectedItem?.Name.Contains(expected, StringComparison.Ordinal) == true,
                5, $"信号断時の動作 {expected}");
            SignalLossStop = stop;
        }

        private void Pause()
        {
            if (App.Button("BtnPlay").Name == "⏸") App.Button("BtnPlay").Invoke();
            WaitUntil(() => App.Button("BtnPlay").Name == "▶", 3, "一時停止");
        }

        public void EnsurePlaying()
        {
            if (App.Button("BtnPlay").Name == "▶") App.Button("BtnPlay").Invoke();
            WaitUntil(() => App.Button("BtnPlay").Name == "⏸", 3, "再生中");
        }

        /// <summary>
        /// SeekBar は 0..1 の比率スライダー（UIA の Maximum も 1）。コミットは
        /// 「値 * 尺」なので、秒ではなく比率を入れる。同じ値だと ValueChanged が
        /// 発火せずコミットされないため、一度揺らしてから目的値へ入れる。
        /// </summary>
        private void Seek(double seconds)
        {
            double target = Math.Max(0, seconds);
            double last = double.NaN;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                WaitMediaReady();
                double duration = PlaybackDuration();
                if (!double.IsFinite(duration) || duration <= 0)
                {
                    Thread.Sleep(250);
                    continue;
                }

                var range = App.Slider("SeekBar").Patterns.RangeValue.Pattern;
                double ratio = Math.Clamp(target / duration, 0, 1);
                if (Math.Abs(range.Value - ratio) < 1e-4)
                {
                    double nudged = Math.Clamp(ratio + (ratio < 0.5 ? 0.01 : -0.01), 0, 1);
                    range.SetValue(nudged);
                }

                range.SetValue(ratio);
                if (TryWaitPosition(target, 0.2, 4))
                {
                    // D26: シーク直後は直前キャンバス（Held）が表示されたまま位置表示だけが
                    // 先に進むことがある。参照採取は新しい世代のフレームが描かれるのを待つ。
                    Thread.Sleep(500);
                    return;
                }
                last = Position();
                Thread.Sleep(200);
            }

            throw new TimeoutException(
                $"シーク {target:F3} に到達しない last={last:F3}; ltc={LtcSeconds():F3}; loaded={LoadedTrackIndex()}");
        }

        private bool TryWaitPosition(double expected, double tolerance, double timeoutSeconds)
        {
            try
            {
                E2EAssert.WaitUntil(
                    () => !App.Process.HasExited && Math.Abs(Position() - expected) <= tolerance,
                    TimeSpan.FromSeconds(timeoutSeconds));
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        /// <summary>
        /// メディアの読み込み完了（TimeLabel と尺が解析可能）を待つ。一時停止ロード直後に
        /// 位置表示が更新されない事象（F1 系）に備え、5 秒ごとにラベル実文字列とアプリログの
        /// 該当行をジャーナルへ残し、シークバー 0 へのヌッジ → 再生/一時停止のヌッジを行い、
        /// それでも駄目なら失敗する。
        /// </summary>
        private void WaitMediaReady(double? maxPosition = null)
        {
            Journal.Write("media-ready-wait", details: new { timeLabel = RawTimeLabel(), maxPosition });
            if (TryWaitMediaReady(5, maxPosition)) return;

            JournalMediaLabelState("media-ready-stale");
            NudgeSeekBarToZero();
            if (TryWaitMediaReady(5, maxPosition)) return;

            NudgePlayPause();
            if (TryWaitMediaReady(5, maxPosition)) return;

            JournalMediaLabelState("media-ready-failed");
            throw new TimeoutException(
                $"メディア読み込み完了に失敗; timeLabel={RawTimeLabel()}; ltc={LtcSeconds():F3}; " +
                $"position={Position():F3}; loaded={LoadedTrackIndex()}");
        }

        private bool TryWaitMediaReady(double timeoutSeconds, double? maxPosition)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
            DateTime nextSample = DateTime.UtcNow;
            while (true)
            {
                double position = Position();
                double duration = PlaybackDuration();
                bool ready = double.IsFinite(position) && double.IsFinite(duration) && duration > 0 &&
                             (maxPosition is null || position <= maxPosition);
                if (ready) return true;
                if (DateTime.UtcNow >= deadline) return false;
                if (DateTime.UtcNow >= nextSample)
                {
                    Journal.Write("media-ready-sample", details: new
                    {
                        timeLabel = RawTimeLabel(),
                        position = JsonNumber(position),
                        duration = JsonNumber(duration),
                        loadedIndex = LoadedTrackIndex(),
                    });
                    nextSample = DateTime.UtcNow.AddSeconds(1);
                }

                Thread.Sleep(100);
            }
        }

        private string RawTimeLabel() => App.Text("TimeLabel");

        /// <summary>非有限値（NaN/±Infinity）は JSON に書けないため null にする。</summary>
        private static double? JsonNumber(double value) =>
            double.IsFinite(value) ? Math.Round(value, 3) : null;

        private void NudgeSeekBarToZero()
        {
            try
            {
                var range = App.Slider("SeekBar").Patterns.RangeValue.Pattern;
                double before = range.Value;
                range.SetValue(Math.Abs(before) < 1e-4 ? 0.01 : 0.0);
                range.SetValue(0.0);
                Journal.Write("media-ready-nudge", details: new { kind = "seekbar-zero", before });
            }
            catch (Exception ex)
            {
                Journal.Write("media-ready-nudge", details: new { kind = "seekbar-zero", error = ex.Message });
            }
        }

        private void NudgePlayPause()
        {
            try
            {
                EnsurePlaying();
                Thread.Sleep(600);
                Pause();
                Journal.Write("media-ready-nudge", details: new { kind = "play-pause" });
            }
            catch (Exception ex)
            {
                Journal.Write("media-ready-nudge", details: new { kind = "play-pause", error = ex.Message });
            }
        }

        /// <summary>失敗・停滞時に、ラベル実文字列とアプリログ末尾（パス・名前は伏せる）を残す。</summary>
        private void JournalMediaLabelState(string eventName)
        {
            try
            {
                Journal.Write(eventName, details: new
                {
                    timeLabel = RawTimeLabel(),
                    metaLine = App.Text("MetaLineText"),
                    ltcText = App.Text("LtcTimecodeText"),
                    playButton = App.Button("BtnPlay").Name,
                    loadedIndex = LoadedTrackIndex(),
                    appLogTail = RunLogLines().TakeLast(20).Select(SanitizeLogLine).ToArray(),
                });
            }
            catch (Exception ex)
            {
                Journal.Write(eventName, details: new { error = ex.Message });
            }
        }

        /// <summary>
        /// ジャーナルに残すアプリログから素材のパスと名前を伏せる。値は空白を含む（現場素材の
        /// ファイル名に空白や括弧がある）ので、次のキー（" xxx=" 形式）か行末までを伏せる。
        /// </summary>
        private static string SanitizeLogLine(string line)
        {
            const string valueUntilNextKey = @"(?:(?!\s+[A-Za-z_][A-Za-z0-9_]*=).)*";
            string sanitized = Regex.Replace(line, @"(path=)" + valueUntilNextKey, "$1<redacted>");
            return Regex.Replace(sanitized, @"(name=)" + valueUntilNextKey, "$1<redacted>");
        }

        private double PlaybackDuration()
        {
            string[] parts = App.Text("TimeLabel").Split('/');
            return parts.Length == 2 ? ParseClock(parts[1].Trim(), MediaFps()) : double.NaN;
        }

        public void LoadTrack(int index, double? readyMaxPosition = 3.0)
        {
            for (int guard = 0; guard < 12; guard++)
            {
                int current = LoadedTrackIndex();
                if (current < 0)
                {
                    WaitUntil(() => LoadedTrackIndex() >= 0, 10, "初回ロードの完了");
                    continue;
                }

                if (current == index)
                {
                    WaitMediaReady();
                    return;
                }

                DateTime issuedAt = DateTime.Now;
                if (current < index) App.Button("BtnNextTrack").Invoke();
                else App.Button("BtnPreviousTrack").Invoke();
                int expectedNext = current + Math.Sign(index - current);
                WaitUntil(() => LoadedTrackIndex() == expectedNext, 10,
                    $"トラック {current} → {expectedNext} のロード");
                WaitForMetadataSince(issuedAt, expectedNext);
                WaitMediaReady(readyMaxPosition);
            }

            throw new TimeoutException($"トラック {index} をロードできない (loaded={LoadedTrackIndex()})");
        }

        /// <summary>
        /// 新しいトラックのロード完了を待つ。目印は FetchMetadata 行、または TimeLabel の尺が
        /// そのトラックの尺になったことに加えて issuedAt 以降のログの
        /// 「Playlist track loaded index=&lt;index&gt;」行。速いロード（キャッシュ済みプロファイル）では
        /// FetchMetadata 行が出ないことがあり、同じ尺の素材では切替前のラベルでも尺が一致するため、
        /// 尺だけには頼らない。
        /// </summary>
        private void WaitForMetadataSince(DateTime issuedAt, int index)
        {
            TrackInfo? track = Tracks.FirstOrDefault(candidate => candidate.Index == index);
            string loadedPattern = $@"Playlist track loaded index={index}\b";
            try
            {
                WaitUntil(
                    () =>
                    {
                        string[] lines = RunLogLinesSince(issuedAt).ToArray();
                        if (lines.Any(line => line.Contains("FetchMetadata:", StringComparison.Ordinal)))
                            return true;
                        return track is not null && TimeLabelShowsDuration(track) &&
                               lines.Any(line => Regex.IsMatch(line, loadedPattern));
                    },
                    15, $"トラック {index} のメタデータ取得");
            }
            catch (TimeoutException)
            {
                JournalMediaLabelState("metadata-wait-timeout");
                throw;
            }
        }

        private bool TimeLabelShowsDuration(TrackInfo track)
        {
            string[] parts = RawTimeLabel().Split('/');
            if (parts.Length != 2) return false;
            double fps = track.FrameRate > 0 ? track.FrameRate : 30.0;
            double shown = ParseClock(parts[1].Trim(), fps);
            return double.IsFinite(shown) && Math.Abs(shown - track.Duration.TotalSeconds) <= 1.0;
        }

        // ---- readings ----

        public double Position()
        {
            string current = App.Text("TimeLabel").Split('/')[0].Trim();
            return ParseClock(current, MediaFps());
        }

        public double LtcSeconds() => ParseClock(App.Text("LtcTimecodeText"), LtcFps);

        /// <summary>
        /// S-1: 着地後の追従を見るための (LTC, 位置) 系列。指定間隔で指定時間サンプルする。
        /// 判定は <see cref="LtcFollowSeries"/> が行い、ここは読み取りだけを担う。
        /// </summary>
        public IReadOnlyList<(double Ltc, double Position)> SampleFollowWindow(TimeSpan duration, TimeSpan interval)
        {
            var samples = new List<(double Ltc, double Position)>();
            DateTime deadline = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadline)
            {
                samples.Add((LtcSeconds(), Position()));
                Thread.Sleep(interval);
            }
            return samples;
        }

        /// <summary>
        /// S-1: 再生中素材の fps。アプリのメタデータ行（FetchMetadata 由来）を優先し、
        /// 無ければプロジェクトの参照 fps を使う。
        /// </summary>
        /// <summary>いま読み込んでいる素材の 1 フレームの秒数（メタ表示の fps から）。</summary>
        private double CurrentFrameSeconds()
        {
            double fps = MediaFps();
            return fps > 0 ? 1.0 / fps : OneFrame;
        }

        public double MediaFps()
        {
            Match rate = Regex.Match(App.Text("MetaLineText"), @"(\d+(?:\.\d+)?)\s*fps");
            if (rate.Success) return double.Parse(rate.Groups[1].Value, CultureInfo.InvariantCulture);
            return Tracks[0].FrameRate;
        }

        private static double ParseClock(string value, double fps)
        {
            string[] parts = value.Split(':');
            if (parts.Length != 4) return double.NaN;
            if (!parts.All(p => double.TryParse(p, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
                return double.NaN;
            double[] n = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return n[0] * 3600 + n[1] * 60 + n[2] + n[3] / fps;
        }

        /// <summary>この run の "Playlist track loaded index=N" の最新値。</summary>
        public int LoadedTrackIndex()
        {
            int index = -1;
            foreach (string line in RunLogLines())
            {
                Match match = Regex.Match(line, @"Playlist track loaded index=(\d+)");
                if (match.Success) index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            return index;
        }

        public int CountLogMatches(string pattern, RegexOptions options = RegexOptions.None) =>
            RunLogLines().Sum(line => Regex.Matches(line, pattern, options).Count);

        public int CountLogMatchesSince(string pattern, DateTime sinceLocal) =>
            RunLogLinesSince(sinceLocal).Sum(line => Regex.Matches(line, pattern).Count);

        // ---- waits / probes ----

        public void WaitUntil(Func<bool> condition, double timeoutSeconds, string description)
        {
            try
            {
                E2EAssert.WaitUntil(() => !App.Process.HasExited && condition(), TimeSpan.FromSeconds(timeoutSeconds));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"{description}; ltc={LtcSeconds():F3}; position={Position():F3}; loaded={LoadedTrackIndex()}", ex);
            }
        }

        public FrameSignature Capture(string imageName) =>
            LtcScenarioFrameProbe.Capture(App, ReportDir, imageName, Journal);

        public FrameSignature WaitForFrame(
            string imageName, TimeSpan timeout, Func<FrameSignature, ReferenceMatch, bool> predicate, string description)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            FrameSignature last = default;
            ReferenceMatch lastMatch = default;
            int attempt = 0;
            while (true)
            {
                attempt++;
                last = Capture($"{imageName}-{attempt:D2}");
                lastMatch = References.Match(last);
                Journal.Write("probe", details: new
                {
                    name = imageName,
                    attempt,
                    isBlack = last.IsBlack,
                    meanLuminance = Math.Round(last.MeanLuminance, 1),
                    best = Describe(lastMatch),
                    ltc = JsonNumber(LtcSeconds()),
                    position = JsonNumber(Position()),
                });
                if (predicate(last, lastMatch)) return last;
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(60);
            }

            throw new TimeoutException(
                $"{description}; isBlack={last.IsBlack} mean=({last.MeanR:F0},{last.MeanG:F0},{last.MeanB:F0}) " +
                $"blackFraction={last.BlackFraction:P1} best={Describe(lastMatch)} ltc={LtcSeconds():F3} position={Position():F3}");
        }

        public void WaitBlack(string name, double timeoutSeconds, string description) =>
            WaitForFrame(name, TimeSpan.FromSeconds(timeoutSeconds), (signature, _) => signature.IsBlack, description);

        /// <summary>
        /// 期待の参照が他の参照と同定閾値未満の距離にあるとき、reference-ambiguous をジャーナルに
        /// 残して true を返す。同定不能の参照では一致判定を失敗にしない。
        /// </summary>
        public bool SkipAmbiguousReference(string name, string trackSymbol, string? kind)
        {
            if (!References.IsAmbiguous(trackSymbol, kind)) return false;
            Journal.Write("reference-ambiguous", details: new
            {
                name,
                symbol = trackSymbol,
                kind,
                note = "expected reference is within the identification threshold of another reference; judgment skipped",
            });
            return true;
        }

        public void WaitReference(string name, string symbol, string? kind, double timeoutSeconds, string description)
        {
            if (SkipAmbiguousReference(name, symbol, kind)) return;
            WaitForFrame(name, TimeSpan.FromSeconds(timeoutSeconds),
                (_, match) => match.MatchesTrack(symbol) && (kind is null || match.Reference!.Kind == kind),
                description);
        }

        /// <summary>黒でなく、参照に一致するなら期待トラックの参照であること（中間位置は参照なしを許容）。</summary>
        public void WaitTrackPicture(string name, TrackInfo track, double timeoutSeconds, string description)
        {
            if (SkipAmbiguousReference(name, track.Symbol, null)) return;
            // 参照が黒の素材では非黒を要求しない（黒と参照静止を区別できない）。
            bool requireNotBlack = BlackJudgmentApplies(track);
            WaitForFrame(name, TimeSpan.FromSeconds(timeoutSeconds),
                (signature, match) => (!requireNotBlack || !signature.IsBlack) &&
                                      (!match.IsMatch || match.MatchesTrack(track.Symbol)),
                description);
        }

        /// <summary>
        /// 黒の判定（黒であること・黒でないこと）を課してよいか。該当トラックの参照に全面黒が
        /// 含まれると「黒」と「参照で静止」を画像で区別できないため、位置と参照一致だけで判定する。
        /// </summary>
        public bool BlackJudgmentApplies(TrackInfo track) =>
            References.HasReferences(track.Symbol) && !References.IsTrackBlack(track.Symbol);

        /// <summary>head 参照が黒でないときだけ非黒を要求してよい（参照が無ければ要求する）。</summary>
        public bool HeadReferenceNotBlack(TrackInfo track) => !References.IsHeadReferenceBlack(track.Symbol);

        /// <summary>
        /// 保持ジャンプの共通判定: 着地区間に入り、進行し、絵が期待トラック側であること。
        /// ランスルー（信号断モードが既定のコンボ index 0）は保持中も動画が走り続けるため、着地は
        /// 区間 [着地目標 − 0.3, 着地目標 + 保持開始（PlayHeld 発行）からの経過秒 + 0.3 + 1/fps] で見る。
        /// 上限が経過秒で伸びるので、着地の所要が長い素材（長 GOP の実素材は 1〜3 秒）でも窓が閉じない。
        /// 停止モードは着地目標 ± 0.3 の固定。着地後は 0.5 秒だけ follow（期待 = 目標 + 経過秒）を残す。
        /// </summary>
        public void CheckHold(
            string name, double ltcTarget, TrackInfo track, double expectedPosition,
            string matrixExpectation, double holdSeconds, bool sampleBlackDuringJump = false)
        {
            // LTC 表示の一致を待ってから位置を見ると、着地して再生が進んだ後に
            // 確認に入り目標±0.3 を通過済みのことがある。送出開始から位置を監視する。
            double sendSeconds = Math.Max(2.5, holdSeconds);
            // D26: ジャンプ発行から着地確認まで 50ms 間隔で画面を採り、黒（黒率 >= 0.99）を数える。
            // 参照が黒の素材では「黒」と「参照で静止」を画像で区別できないため数えない。
            // 発行直前が黒（黒率 >= 0.99）のときも数えない（Held が黒のまま残っているだけで、
            // テスト開始直後の最初のジャンプが該当する）。
            bool blackJudgment = BlackJudgmentApplies(track);
            bool jumpBlackExempt = false;
            double beforeJumpBlackFraction = 0.0;
            if (sampleBlackDuringJump)
            {
                FrameSignature beforeJump = Capture($"{name}-before-jump");
                beforeJumpBlackFraction = beforeJump.BlackFraction;
                jumpBlackExempt = JumpBlackPolicy.IsExempt(beforeJumpBlackFraction);
                if (jumpBlackExempt)
                    Journal.Write("jump-black-before", details: new
                    {
                        name,
                        blackFraction = Math.Round(beforeJumpBlackFraction, 4),
                        note = "ジャンプ発行直前が黒のため jump-black を数えない",
                    });
            }

            DateTime holdStart = DateTime.UtcNow;
            Signal.PlayHeld(ltcTarget, LtcFps, TimeSpan.FromSeconds(sendSeconds));
            bool runThrough = !SignalLossStop;
            double landingTolerance = PositionToleranceSeconds;
            double frameAllowance = OneFrame;

            var jumpSamples = new List<FrameSignature>();
            var follow = new List<(double Elapsed, double Expected, double Observed)>();
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(sendSeconds + 1);
            DateTime nextSample = DateTime.UtcNow;
            bool landed = false;
            DateTime followUntil = DateTime.MaxValue;
            double lastElapsed = 0.0;
            double lastExpected = double.NaN;
            double lastObserved = double.NaN;
            (double Min, double Max) lastRange = (double.NaN, double.NaN);
            while (true)
            {
                lastElapsed = (DateTime.UtcNow - holdStart).TotalSeconds;
                lastExpected = HoldLandingExpectation.ExpectedPosition(expectedPosition, lastElapsed, runThrough);
                lastRange = HoldLandingExpectation.LandingRange(
                    expectedPosition, lastElapsed, runThrough, landingTolerance, frameAllowance);
                lastObserved = Position();
                if (!landed && HoldLandingExpectation.IsLanded(lastObserved, lastRange.Min, lastRange.Max))
                {
                    landed = true;
                    followUntil = DateTime.UtcNow.AddMilliseconds(500);
                    Journal.Write("hold-landing", details: new
                    {
                        name,
                        runThrough,
                        mappedTarget = expectedPosition,
                        elapsedSeconds = Math.Round(lastElapsed, 3),
                        observed = Math.Round(lastObserved, 3),
                        rangeMin = Math.Round(lastRange.Min, 3),
                        rangeMax = Math.Round(lastRange.Max, 3),
                        expected = Math.Round(lastExpected, 3),
                    });
                }
                else if (landed)
                {
                    // 着地後は 0.5 秒だけ「期待どおり進行しているか」の観測を残す（失敗条件にしない）。
                    follow.Add((lastElapsed, lastExpected, lastObserved));
                    if (DateTime.UtcNow >= followUntil) break;
                }

                if (DateTime.UtcNow >= deadline) break;
                if (!landed && sampleBlackDuringJump && !jumpBlackExempt && blackJudgment && DateTime.UtcNow >= nextSample)
                {
                    jumpSamples.Add(Capture($"jump-black-{name}-{jumpSamples.Count + 1:D2}"));
                    nextSample = DateTime.UtcNow.AddMilliseconds(50);
                }

                Thread.Sleep(50);
            }

            if (follow.Count > 0)
            {
                Journal.Write("hold-follow", details: new
                {
                    name,
                    samples = follow.Count,
                    maxErrorSeconds = Math.Round(follow.Max(sample => Math.Abs(sample.Observed - sample.Expected)), 3),
                    lastElapsed = Math.Round(follow[^1].Elapsed, 3),
                    lastExpected = Math.Round(follow[^1].Expected, 3),
                    lastObserved = Math.Round(follow[^1].Observed, 3),
                });
            }

            if (sampleBlackDuringJump)
            {
                if (jumpBlackExempt)
                {
                    Journal.Write("jump-black-summary", details: new
                    {
                        name,
                        skipped = true,
                        reason = "before-jump-is-black",
                        blackFraction = Math.Round(beforeJumpBlackFraction, 4),
                    });
                }
                else if (blackJudgment)
                {
                    int blackFrames = jumpSamples.Count(sample => sample.IsBlack);
                    double worst = jumpSamples.Count == 0 ? 1.0 : jumpSamples.Min(sample => sample.BlackFraction);
                    Journal.Write("jump-black-summary", details: new
                    {
                        name,
                        samples = jumpSamples.Count,
                        blackFrames,
                        minBlackFraction = Math.Round(worst, 4),
                    });
                    blackFrames.Should().Be(0, $"{name}: ジャンプ中（発行〜着地）に黒を挟まない");
                }
                else
                {
                    Journal.Write("jump-black-summary", details: new
                    {
                        name,
                        skipped = true,
                        reason = "reference-is-black",
                        symbol = track.Symbol,
                    });
                }
            }

            WaitUntil(() => Math.Abs(LtcSeconds() - ltcTarget) <= 0.05, 6, $"保持 LTC {ltcTarget:F2} の受信");
            Journal.Write("hold", details: new { target = ltcTarget, observed = LtcSeconds() });
            landed.Should().BeTrue(
                $"{name}: 位置が着地区間 {(runThrough ? $"[{expectedPosition:F3} - 0.3, {expectedPosition:F3} + 経過秒 + 0.3 + 1/fps]" : $"[{expectedPosition:F3} ± 0.3]")}" +
                $" に入る (runThrough={runThrough} elapsed={lastElapsed:F3} range=[{lastRange.Min:F3}, {lastRange.Max:F3}]" +
                $" expected={lastExpected:F3} observed={lastObserved:F3})");
            double observed = Position();
            FrameSignature signature = Capture($"hold-{name}");
            ReferenceMatch match = References.Match(signature);
            Journal.Write("hold-observation", details: new
            {
                name,
                symbol = track.Symbol,
                ltcTarget,
                expectedPosition,
                observedPosition = observed,
                matrixExpectation = Expectation(matrixExpectation),
                isBlack = signature.IsBlack,
                blackJudgment,
                nearestKnownColor = LtcScenarioFrameProbe.DescribeNearestKnownColor(signature),
                best = Describe(match),
            });
            if (blackJudgment)
                signature.IsBlack.Should().BeFalse($"{name}: トラックの保持中に黒にならない");
            if (SkipAmbiguousReference(name, track.Symbol, null))
            {
                // 同定不能（期待の参照が他の参照と閾値未満）: 参照一致の判定は失敗にしない。
            }
            else if (match.IsMatch)
                match.MatchesTrack(track.Symbol).Should().BeTrue(
                    $"{name}: 参照に一致するなら {track.Symbol} の参照であること（実際: {Describe(match)}）");
        }

        public void CheckGap(string name, double ltcTarget)
        {
            Hold(ltcTarget, 3.0);
            FrameSignature signature = WaitForFrame($"{name}-gap", TimeSpan.FromSeconds(1.5),
                (frame, _) => frame.IsBlack, $"{name}: ギャップの保持で黒");
            Journal.Write("gap-observation", details: new
            {
                name,
                ltcTarget,
                isBlack = signature.IsBlack,
                meanLuminance = Math.Round(signature.MeanLuminance, 1),
            });
        }

        public void CheckFreeze(string name, double ltcTarget, TrackInfo previousTrack)
        {
            Hold(ltcTarget, 3.5);
            if (SkipAmbiguousReference(name, previousTrack.Symbol, "tail")) return;
            bool ExpectTail(FrameSignature _, ReferenceMatch match) =>
                match.MatchesTrack(previousTrack.Symbol) && match.Reference!.Kind == "tail";
            DateTime started = DateTime.UtcNow;
            FrameSignature signature;
            try
            {
                signature = WaitForFrame($"{name}-freeze", TimeSpan.FromSeconds(3.0),
                    (frame, match) => ExpectTail(frame, match),
                    $"{name}: Freeze は {previousTrack.Symbol} の最終フレーム");
            }
            catch (TimeoutException ex)
            {
                // A1 §4.5: 3.0 秒はハンドラの timeout と同値で、遅れて更新と更新されないを区別できない。
                // 合否の基準（3.0 秒）は変えず、判定が外れたときだけ最大 8 秒まで 200ms 間隔で
                // 遅れての更新を観測し、一致時刻を freeze-late-update に残す（所要は失敗時だけ延びる）。
                double? lateUpdateSeconds = null;
                FrameSignature lateSignature = default;
                for (int attempt = 1; (DateTime.UtcNow - started).TotalSeconds < 8.0; attempt++)
                {
                    Thread.Sleep(200);
                    FrameSignature candidate = Capture($"{name}-freeze-late-{attempt:D2}");
                    ReferenceMatch match = References.Match(candidate);
                    if (ExpectTail(candidate, match))
                    {
                        lateUpdateSeconds = (DateTime.UtcNow - started).TotalSeconds;
                        lateSignature = candidate;
                        break;
                    }
                }

                Journal.Write("freeze-late-update", details: new
                {
                    name,
                    seconds = lateUpdateSeconds is double seconds ? Math.Round(seconds, 3) : (double?)null,
                    note = lateUpdateSeconds is null ? "8 秒まで更新なし" : "3.0 秒の判定後に遅れて一致",
                    nearestKnownColor = lateUpdateSeconds is null
                        ? null
                        : LtcScenarioFrameProbe.DescribeNearestKnownColor(lateSignature),
                });
                throw new TimeoutException(
                    $"{ex.Message}; 遅れての観測: " + (lateUpdateSeconds is double late
                        ? $"{late:F3} 秒で {previousTrack.Symbol} の最終フレームに更新（合否の基準は 3.0 秒のまま）"
                        : "8 秒まで更新なし"), ex);
            }

            Journal.Write("freeze-observation", details: new
            {
                name,
                ltcTarget,
                symbol = previousTrack.Symbol,
                nearestKnownColor = LtcScenarioFrameProbe.DescribeNearestKnownColor(signature),
            });
        }

        public string Expectation(string defaultProjectLabel) =>
            IsDefaultProject ? defaultProjectLabel : "実素材: 位置のみ";

        private static string Describe(ReferenceMatch match) => match.Reference is null
            ? "no-reference"
            : $"{match.Reference.TrackSymbol}/{match.Reference.Kind} d={match.ColorDistance:F1} px={match.PixelDifference:F2} match={match.IsMatch}";

        public IReadOnlyList<PerfSegment> PerfSegmentsSince(DateTime sinceLocal)
        {
            var segments = new List<PerfSegment>();
            foreach (string line in RunLogLinesSince(sinceLocal))
            {
                if (!line.Contains("Playback perf", StringComparison.Ordinal)) continue;
                Match timestamp = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
                Match elapsed = Regex.Match(line, @"elapsed=([\d.]+)s");
                Match frames = Regex.Match(line, @"frameUpdates=(\d+)");
                if (!timestamp.Success || !elapsed.Success || !frames.Success) continue;
                if (!DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime at)) continue;
                segments.Add(new PerfSegment(at,
                    double.Parse(elapsed.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(frames.Groups[1].Value, CultureInfo.InvariantCulture)));
            }

            return segments;
        }

        /// <summary>
        /// L-1: 補正シークの発行（"Timecode sync seek ltc=... success=true"）から、その保留が
        /// セトル／タイムアウトした行までの秒数（着地の指標）。セトルしなかった発行
        /// （次のシークに置き換わったもの）は数えない。
        /// </summary>
        public IReadOnlyList<double> CorrectionSeekSettleSecondsSince(DateTime sinceLocal)
        {
            var durations = new List<double>();
            DateTime? issuedAt = null;
            foreach (string line in RunLogLinesSince(sinceLocal))
            {
                Match timestamp = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
                if (!timestamp.Success ||
                    !DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime at))
                    continue;

                if (line.Contains("Timecode sync seek ltc=", StringComparison.Ordinal) &&
                    line.Contains("success=true", StringComparison.Ordinal))
                {
                    issuedAt = at;
                    continue;
                }
                if (issuedAt is not { } issue)
                    continue;
                bool settled = line.Contains("Timecode sync pending \"Settled\"", StringComparison.Ordinal);
                bool timedOut = line.Contains("Timecode sync pending \"TimedOut\"", StringComparison.Ordinal);
                if (!settled && !timedOut)
                    continue;
                durations.Add((at - issue).TotalSeconds);
                issuedAt = null;
            }
            return durations;
        }

        public int StressCycles(int defaultValue)
        {
            string? raw = Environment.GetEnvironmentVariable(CyclesVariable);
            return int.TryParse(raw, out int parsed) ? Math.Clamp(parsed, 1, 100) : defaultValue;
        }

        // ---- exit / evidence ----

        public void VerifyAndExit()
        {
            bool exited = App.ExitNormally(TimeSpan.FromSeconds(15));
            int? exitCode = null;
            if (exited)
            {
                try { exitCode = App.Process.ExitCode; } catch (InvalidOperationException) { }
            }

            _exited = exited;
            Journal.Write("app-exit", details: new { exited, exitCode });
            exited.Should().BeTrue("この run でアプリが正常終了する");
            exitCode.Should().Be(0);

            string[] errors = RunLogLines()
                .Where(line => line.Contains(" [ERR] ", StringComparison.Ordinal) ||
                               line.Contains(" [FTL] ", StringComparison.Ordinal))
                .ToArray();
            Journal.Write("err-scan", details: new { errFtl = errors.Length, sample = errors.Take(5).ToArray() });
            errors.Should().BeEmpty("この run のアプリログに ERR/FTL が無い");

            Process[] leftovers = FindResidualProcesses();
            Journal.Write("residual-processes", details: leftovers.Select(p => p.Id).ToArray());
            leftovers.Should().BeEmpty("この run が起動したアプリの残プロセスが無い");
        }

        private Process[] FindResidualProcesses() =>
            Process.GetProcessesByName("TimecodeSyncPlayer")
                .Where(process =>
                {
                    try { return process.StartTime >= _startedAt.AddSeconds(-2); }
                    catch { return false; }
                })
                .ToArray();

        public void Dispose()
        {
            try { Signal?.Stop(); } catch { /* 破棄は失敗しても続ける */ }
            try { Signal?.Dispose(); } catch { /* 破棄は失敗しても続ける */ }
            if (!_exited)
            {
                try { App?.ExitNormally(TimeSpan.FromSeconds(10)); } catch { /* Dispose が kill する */ }
            }

            App?.Dispose();
            Journal.Dispose();
        }

        // ---- log access ----

        private IEnumerable<string> RunLogLines() => RunLogLinesSince(_startedAt);

        private IEnumerable<string> RunLogLinesSince(DateTime sinceLocal)
        {
            string logDir = Path.Combine(Path.GetDirectoryName(_exePath)!, "logs");
            if (!Directory.Exists(logDir)) yield break;
            FileInfo? newest = new DirectoryInfo(logDir).GetFiles("timecodesyncplayer-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) yield break;

            string text;
            using (var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();

            foreach (string line in text.Split('\n'))
            {
                Match timestamp = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
                if (!timestamp.Success ||
                    !DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime at) ||
                    at < sinceLocal)
                    continue;
                yield return line;
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimecodeSyncPlayer.slnx")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("リポジトリルートが見つかりません。");
        return dir.FullName;
    }
}
