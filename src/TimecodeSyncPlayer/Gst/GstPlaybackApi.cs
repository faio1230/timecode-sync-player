using System;
using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// IPlaybackApi の GStreamer 実装。shim（IGstNativeApi）を直接呼び、
/// 文字列コマンドの生成と再解析（GstCommandTranslator）を経由しない。
/// seeking は既存の文字列経路と同じ <see cref="GstSeekingTracker"/> を共有する。
/// </summary>
internal sealed class GstPlaybackApi : IPlaybackApi
{
    private readonly GstBackendState _state;

    public GstPlaybackApi(GstBackendState state)
    {
        _state = state;
    }

    private IntPtr Player => _state.Player;

    public PlaybackResult Load(string path, double? startSeconds, bool paused)
    {
        if (string.IsNullOrWhiteSpace(path))
            return PlaybackResult.Fail("path is empty");
        IntPtr player = Player;
        if (player == IntPtr.Zero)
            return PlaybackResult.Fail("player is not created");

        _state.Seeking.Clear();
        _state.IsPaused = paused;
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
            int rc = _state.Native.SetSpeed(player, rate);
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
            return _state.Native.TryGetTimePos(player, out seconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstPlaybackApi.TryGetTimePos 失敗");
            seconds = 0;
            return false;
        }
    }

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
