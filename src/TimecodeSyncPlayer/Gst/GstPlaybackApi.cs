using System;
using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// IPlaybackApi の GStreamer 実装。shim（IGstNativeApi）を直接呼び、
/// 文字列コマンドの生成と再解析を経由しない。
/// seeking は既存の文字列経路と同じ <see cref="GstSeekingTracker"/> を共有する。
/// </summary>
internal sealed class GstPlaybackApi : IPlaybackApi
{
    private readonly GstBackendState _state;
    // 0.4.5-A: 旧 DLL に _ex が無い場合、1 回だけ警告して以後は旧経路に固定する。
    private bool _timePosExUnavailable;
    // 0.4.5-C: 旧 DLL に GOP getter が無い場合、警告を無効化して固定する。
    private bool _gopStatusUnavailable;

    public GstPlaybackApi(GstBackendState state)
    {
        _state = state;
    }

    /// <summary>
    /// 0.4.7: フレームの流れを乱す操作と、指示した再生速度の台帳。ネイティブ操作は必ずここを
    /// 通るので、呼び出し元に関係なく記録が漏れない（「デコードが追いついていない」表示の判定に使う）。
    /// </summary>
    public PlaybackActivityLedger Activity { get; } = new();

    /// <summary>
    /// 0.4.8: 位置照会の連続性。同じ系列（乱れの回数・世代）の中では位置を後退させず、後退を
    /// 「不安定」として数える。照会は必ずここを通るので、同期・表示・出力が同じ値を見る。
    /// </summary>
    public PlaybackPositionContinuity PositionContinuity { get; } = new();

    /// <summary>0.4.8: 直近に位置が後退した（パイプライン位置を補正の入力として信用できない）か。</summary>
    public bool IsPositionUnstable => PositionContinuity.IsUnstable();

    private IntPtr Player => _state.Player;

    /// <summary>
    /// セッション初期化: player を生成し、開始状態（pause=yes 相当）を整える。
    /// 旧 mpv 経路のセッション生成と開始プロパティ適用の役割をここへ集約する。
    /// </summary>
    public PlaybackResult Initialize()
    {
        if (!_state.EnsurePlayer())
        {
            string error = string.IsNullOrEmpty(_state.LastError)
                ? "player create failed"
                : _state.LastError;
            Log.Error("GstPlaybackApi: プレイヤー生成失敗 {Error}", error);
            return PlaybackResult.Fail(error);
        }

        IntPtr player = _state.Player;
        _state.IsPaused = true;
        try
        {
            int rc = _state.Native.SetPaused(player, true);
            if (rc != 0)
                Log.Warning("GstPlaybackApi: 初期 pause 設定に失敗 rc={Rc}", rc);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.Initialize 失敗");
            return PlaybackResult.Fail(ex.Message);
        }
        Log.Information("GstPlaybackApi: プレイヤー初期化完了 sender='{Sender}'", _state.SenderName);
        return PlaybackResult.Ok;
    }

    public PlaybackResult Load(string path, double? startSeconds, bool paused)
    {
        if (string.IsNullOrWhiteSpace(path))
            return PlaybackResult.Fail("path is empty");
        IntPtr player = Player;
        if (player == IntPtr.Zero)
            return PlaybackResult.Fail("player is not created");

        _state.Seeking.Clear();
        _state.IsPaused = paused;
        Activity.NoteDisturbance();
        try
        {
            long started = Stopwatch.GetTimestamp();
            int rc = _state.Native.Load(player, path, startSeconds ?? -1.0, paused, out string error);
            // S4 計測: shim 呼び出し 1 回の実時間（文字列経路と同じ形式で残す）。
            Log.Information(
                "Gst loadfile path={Path} start={Start} paused={Paused} rc={Rc} elapsedMs={ElapsedMs:F1}",
                path, startSeconds, paused, rc,
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
            if (rc != 0)
            {
                Log.Warning("GstPlaybackApi: load 失敗 path={Path} err={Error}", path, error);
                return PlaybackResult.Fail(string.IsNullOrEmpty(error) ? $"load failed rc={rc}" : error);
            }
            // shim はロードで速度を 1.0 に戻す。
            Activity.NoteRate(1.0);
            return PlaybackResult.Ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.Load 失敗 path={Path}", path);
            return PlaybackResult.Fail(ex.Message);
        }
    }

    public PlaybackResult Seek(double seconds)
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero)
            return PlaybackResult.Fail("player is not created");
        if (!double.IsFinite(seconds))
            return PlaybackResult.Fail("seek seconds is not finite");
        try
        {
            ulong baseline = _state.Seeking.ReadArrivalBaseline(player);
            Activity.NoteDisturbance();
            ulong generation = _state.Native.Seek(player, Math.Max(seconds, 0.0));
            if (generation == 0)
                return PlaybackResult.Fail("seek was rejected");
            // 基準はシーク前の到着数。新位置のフレームが届くまで IsSeeking = true。
            _state.Seeking.MarkPending(baseline);
            return PlaybackResult.Ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.Seek 失敗 seconds={Seconds}", seconds);
            return PlaybackResult.Fail(ex.Message);
        }
    }

    public PlaybackResult Stop()
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero)
            return PlaybackResult.Fail("player is not created");
        _state.Seeking.Clear();
        Activity.NoteDisturbance();
        try
        {
            int rc = _state.Native.Stop(player);
            return rc == 0 ? PlaybackResult.Ok : PlaybackResult.Fail($"stop failed rc={rc}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.Stop 失敗");
            return PlaybackResult.Fail(ex.Message);
        }
    }

    public PlaybackResult SetPaused(bool paused)
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero)
            return PlaybackResult.Fail("player is not created");
        _state.IsPaused = paused;
        Activity.NoteDisturbance();
        try
        {
            int rc = _state.Native.SetPaused(player, paused);
            return rc == 0 ? PlaybackResult.Ok : PlaybackResult.Fail($"set paused failed rc={rc}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.SetPaused 失敗 paused={Paused}", paused);
            return PlaybackResult.Fail(ex.Message);
        }
    }

    public PlaybackResult SetRate(double rate)
    {
        if (!(rate > 0))
            return PlaybackResult.Fail("rate must be positive");
        IntPtr player = Player;
        if (player == IntPtr.Zero)
            return PlaybackResult.Fail("player is not created");
        try
        {
            // 速度切替（フラッシュあり）はフレームの流れを乱す。
            Activity.NoteDisturbance();
            int rc = _state.Native.SetSpeed(player, rate);
            if (rc == 0) Activity.NoteRate(rate);
            return rc == 0 ? PlaybackResult.Ok : PlaybackResult.Fail($"set rate failed rc={rc}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.SetRate 失敗 rate={Rate}", rate);
            return PlaybackResult.Fail(ex.Message);
        }
    }

    public PlaybackResult SetRateInstant(double rate)
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero)
            return PlaybackResult.Fail("player is not created");
        try
        {
            int rc = _state.Native.SetRateInstant(player, rate);
            if (rc == 0) Activity.NoteRate(rate);
            return rc == 0 ? PlaybackResult.Ok : PlaybackResult.Fail($"set rate instant failed rc={rc}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.SetRateInstant 失敗 rate={Rate}", rate);
            return PlaybackResult.Fail(ex.Message);
        }
    }

    public void SetVolume(double volume0To100)
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero)
        {
            Log.Warning("GstPlaybackApi: volume 設定をスキップ（player 未作成）");
            return;
        }
        try
        {
            int rc = _state.Native.SetVolume(player, volume0To100);
            if (rc != 0)
                Log.Warning("GstPlaybackApi: volume 設定失敗 volume={Volume} rc={Rc}", volume0To100, rc);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.SetVolume 失敗 volume={Volume}", volume0To100);
        }
    }

    public void SetMute(bool mute)
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero)
        {
            Log.Warning("GstPlaybackApi: mute 設定をスキップ（player 未作成）");
            return;
        }
        try
        {
            int rc = _state.Native.SetMute(player, mute);
            if (rc != 0)
                Log.Warning("GstPlaybackApi: mute 設定失敗 mute={Mute} rc={Rc}", mute, rc);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.SetMute 失敗 mute={Mute}", mute);
        }
    }

    public bool TryGetTimePos(out double seconds)
    {
        seconds = 0;
        IntPtr player = Player;
        if (player == IntPtr.Zero) return false;
        try
        {
            if (!_state.Native.TryGetTimePos(player, out seconds))
                return false;
            seconds = PositionContinuity.Observe(seconds, Activity.Disturbances, generation: 0);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.TryGetTimePos 失敗");
            seconds = 0;
            return false;
        }
    }

    public bool TryGetPositionSample(out PlaybackPositionSample sample)
    {
        sample = default;
        IntPtr player = Player;
        if (player == IntPtr.Zero) return false;

        if (!_timePosExUnavailable)
        {
            try
            {
                if (!_state.Native.TryGetTimePosEx(player, out GstNative.TcsPositionSample native))
                    return false;
                sample = MapPositionSample(native);
                sample = sample with
                {
                    Seconds = PositionContinuity.Observe(sample.Seconds, Activity.Disturbances, sample.CurrentGeneration),
                };
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                // 0.4.5-A: 旧 DLL（_ex なし）。1 回だけ警告し、以後は旧経路に固定する。
                _timePosExUnavailable = true;
                Log.Warning("GstPlaybackApi: tcs_player_get_time_pos_ex が DLL に無いため旧経路（TryGetTimePos）へフォールバックします");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GstPlaybackApi.TryGetPositionSample 失敗");
                return false;
            }
        }

        if (!TryGetTimePos(out double seconds))
            return false;
        sample = new PlaybackPositionSample(seconds, PlaybackPositionBasis.Pipeline, 0, 0, 0, 0);
        return true;
    }

    private static PlaybackPositionSample MapPositionSample(GstNative.TcsPositionSample native) =>
        new(native.Seconds,
            native.Basis switch
            {
                1 => PlaybackPositionBasis.Pipeline,
                2 => PlaybackPositionBasis.Delivered,
                _ => PlaybackPositionBasis.None,
            },
            native.Generation,
            native.DeliveredSeconds,
            native.DeliveredGeneration,
            native.CurrentGeneration);

    public bool TryGetDuration(out double seconds)
    {
        seconds = 0;
        IntPtr player = Player;
        if (player == IntPtr.Zero) return false;
        try
        {
            return _state.Native.TryGetDuration(player, out seconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.TryGetDuration 失敗");
            seconds = 0;
            return false;
        }
    }

    public bool TryGetFps(out double fps)
    {
        fps = 0;
        IntPtr player = Player;
        if (player == IntPtr.Zero) return false;
        try
        {
            return _state.Native.TryGetFps(player, out fps);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.TryGetFps 失敗");
            fps = 0;
            return false;
        }
    }

    /// <summary>
    /// 0.4.5-C: ロング GOP 警告のポーリング。active=0 は「未計測」であり
    /// 「異常なし」ではない（UI は何も出さない）。表示だけの診断値。
    /// </summary>
    // 0.4.5-C3: 読み込み時の静的スキャン。プレイヤー不要で、再生経路には触れない。
    // コンテナを読むだけ（デコードしない）が I/O は待つので、UI スレッドから呼ばないこと。
    private static bool _scanGopUnavailable;

    /// <summary>素材のキーフレーム分布を測る。測れなければ null。</summary>
    /// <remarks>
    /// 所要はファイルの大きさにほぼ比例する。実測: 928MB で 0.27 秒、13.7GB で 11.1 秒
    /// （検証機、SATA SSD）。上限は 60 秒にしてある——13.7GB の 5 倍強の余裕があり、
    /// より遅いストレージでも届きにくい。**失敗・時間切れは必ずログに残す**。
    /// 黙って「判定しない」に落ちると、警告が出ない理由が「短い GOP だから」なのか
    /// 「スキャンが終わっていないから」なのか区別できない（検証機が実際に誤解した）。
    /// </remarks>
    public static GopScanResult? ScanGop(string path, int budgetMs = 0)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_scanGopUnavailable) return null;
        // 再生と同じディスクを読むので、読み取りの優先度を下げる。デコーダの読みと
        // 取り合いになったとき、こちらが譲る（解析は遅れてよいが、再生は遅れてはいけない）。
        System.Threading.ThreadPriority previous = System.Threading.Thread.CurrentThread.Priority;
        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Lowest;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int rc = GstNative.Imports.tcs_scan_gop(path, budgetMs, out GstNative.TcsGopScan n);
            sw.Stop();
            if (rc != 0)
            {
                Log.Warning(
                    "GOP scan failed: rc={Rc} elapsedMs={Elapsed} budgetMs={Budget} path={Path}",
                    rc, sw.ElapsedMilliseconds, budgetMs, path);
                return null;
            }
            if (n.Keyframes < 2)
            {
                // キーフレームが取れない素材（実測: AV1 でパーサが DELTA_UNIT を立てない）。
                // 判定にもヒントにも使えないが、「測ったが使えなかった」ことは残す。
                Log.Warning(
                    "GOP scan unusable: keyframes={Keyframes} durationSec={Duration:F1} elapsedMs={Elapsed} path={Path}",
                    n.Keyframes, n.DurationSec, sw.ElapsedMilliseconds, path);
            }
            else
            {
                Log.Information(
                    "GOP scan: keyframes={Keyframes} maxGapMs={Max:F0} elapsedMs={Elapsed} path={Path}",
                    n.Keyframes, n.MaxGapSec * 1000.0, sw.ElapsedMilliseconds, path);
            }
            if (n.Truncated != 0)
            {
                Log.Warning(
                    "GOP scan truncated: keyframes={Keyframes} maxGapMs={Max:F0} elapsedMs={Elapsed} path={Path}",
                    n.Keyframes, n.MaxGapSec * 1000.0, sw.ElapsedMilliseconds, path);
            }
            return new GopScanResult(
                n.Keyframes, n.Truncated != 0, n.DurationSec, n.HeadGapSec, n.TailGapSec,
                n.MedianGapSec, n.P95GapSec, n.MaxGapSec);
        }
        catch (EntryPointNotFoundException)
        {
            // 旧 DLL（スキャンなし）。1 回だけ警告し、以後は測らない。
            _scanGopUnavailable = true;
            Log.Warning("GstPlaybackApi: tcs_scan_gop が DLL に無いため素材のキーフレーム解析を無効化します");
            return null;
        }
        catch (DllNotFoundException)
        {
            _scanGopUnavailable = true;
            return null;
        }
        finally
        {
            System.Threading.Thread.CurrentThread.Priority = previous;
        }
    }

    public bool TryGetGopStatus(out GopStatus status)
    {
        status = default;
        IntPtr player = Player;
        if (player == IntPtr.Zero) return false;
        if (_gopStatusUnavailable) return false;
        try
        {
            if (!_state.Native.TryGetGopStatus(player, out GstNative.TcsGopStatus native))
                return false;
            status = MapGopStatus(native);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            // 旧 DLL（GOP getter なし）。1 回だけ警告し、以後は何も出さない。
            _gopStatusUnavailable = true;
            Log.Warning("GstPlaybackApi: tcs_player_get_gop_status が DLL に無いためロング GOP 警告を無効化します");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.TryGetGopStatus 失敗");
            return false;
        }
    }

    private static GopStatus MapGopStatus(GstNative.TcsGopStatus native) =>
        new(native.State,
            native.Active != 0,
            native.Keyframes,
            native.MedianIntervalSec,
            native.PendingSec,
            native.ThresholdSec,
            native.WarningQpc);

    public string GetPath()
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero) return string.Empty;
        try
        {
            return _state.Native.GetPath(player);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.GetPath 失敗");
            return string.Empty;
        }
    }

    public bool TryGetSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        IntPtr player = Player;
        if (player == IntPtr.Zero) return false;
        try
        {
            return _state.Native.TryGetSize(player, out width, out height);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.TryGetSize 失敗");
            width = 0;
            height = 0;
            return false;
        }
    }

    public string GetVideoCodec()
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero) return string.Empty;
        try
        {
            return _state.Native.DecoderName(player);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.GetVideoCodec 失敗");
            return string.Empty;
        }
    }

    public bool IsPaused()
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero) return false;
        try
        {
            return _state.Native.IsPaused(player);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.IsPaused 失敗");
            return false;
        }
    }

    /// <summary>
    /// player 未作成は位置が定まっていないため true（旧 GetPropertyString("seeking") の
    /// 空文字を「シーク中」として扱っていたのと同じ）。
    /// </summary>
    public bool IsSeeking()
    {
        IntPtr player = Player;
        if (player == IntPtr.Zero) return true;
        try
        {
            return _state.Seeking.IsSeeking(player);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.IsSeeking 失敗");
            return false;
        }
    }
}

/// <summary>
/// 0.4.5-C: shim のロング GOP 検出スナップショット。State は
/// <see cref="LongGopWarningMonitor.StateMeasuring"/> /
/// <see cref="LongGopWarningMonitor.StateWarning"/>。MedianIntervalSeconds は
/// 実測したキーフレーム間隔の中央値（C2。0 = 未確定）。
/// </summary>
internal readonly record struct GopStatus(
    int State,
    bool Active,
    ulong Keyframes,
    double MedianIntervalSeconds,
    double PendingSeconds,
    double ThresholdSeconds,
    ulong WarningQpc);
