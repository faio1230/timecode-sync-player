using System;
using System.Diagnostics;
using System.Globalization;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// IMpvApi の GStreamer 実装。既存MainWindow系の mpv 文字列コマンド/プロパティを
/// tcs_gstreamer の呼び出しへ翻訳する（mpv 経路は変更せず併存）。
/// 未対応の表示系プロパティ (osd-*) や初期化スイッチ (vo/hwdec/keep-open) は無視する。
/// </summary>
internal sealed class GstMpvApiAdapter : IMpvApi
{
    private readonly GstBackendState _state;

    public GstMpvApiAdapter(GstBackendState state)
    {
        _state = state;
    }

    public int FormatDouble => 0;

    public IntPtr Create()
    {
        return _state.EnsurePlayer() ? _state.Player : IntPtr.Zero;
    }

    public int Initialize(IntPtr ctx) => ctx != IntPtr.Zero ? 0 : -1;

    public void TerminateDestroy(IntPtr ctx) => _state.DisposePlayer();

    public int SetPropertyString(IntPtr ctx, string name, string value)
    {
        if (ctx == IntPtr.Zero) return -1;
        try
        {
            switch (name)
            {
                case "pause":
                    bool paused = value is "yes" or "1" or "true" or "on";
                    _state.IsPaused = paused;
                    return _state.Native.SetPaused(ctx, paused);
                case "volume":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double vol))
                        return _state.Native.SetVolume(ctx, vol);
                    return -1;
                case "mute":
                    return _state.Native.SetMute(ctx, value is "yes" or "1" or "true" or "on");
                case "speed":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate)
                        && rate > 0)
                        return _state.Native.SetSpeed(ctx, rate);
                    return -1;
                default:
                    // vo / hwdec / keep-open / osd-* などは GStreamer 側では意味を持たない。
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstMpvApiAdapter.SetPropertyString {Name} 失敗", name);
            return -1;
        }
    }

    public int GetProperty(IntPtr ctx, string name, int format, out double result)
    {
        result = 0;
        if (ctx == IntPtr.Zero) return -1;
        try
        {
            switch (name)
            {
                case "time-pos":
                    return _state.Native.TryGetTimePos(ctx, out result) ? 0 : -1;
                case "duration":
                    return _state.Native.TryGetDuration(ctx, out result) ? 0 : -1;
                case "container-fps":
                    return _state.Native.TryGetFps(ctx, out result) ? 0 : -1;
                default:
                    return -1;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstMpvApiAdapter.GetProperty {Name} 失敗", name);
            return -1;
        }
    }

    public string GetPropertyString(IntPtr ctx, string name)
    {
        if (ctx == IntPtr.Zero) return string.Empty;
        try
        {
            switch (name)
            {
                case "path":
                    return _state.Native.GetPath(ctx);
                case "width":
                case "height":
                    if (_state.Native.TryGetSize(ctx, out int w, out int h))
                        return (name == "width" ? w : h).ToString(CultureInfo.InvariantCulture);
                    return string.Empty;
                case "video-codec":
                    return _state.Native.DecoderName(ctx);
                default:
                    return string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstMpvApiAdapter.GetPropertyString {Name} 失敗", name);
            return string.Empty;
        }
    }

    public int CommandString(IntPtr ctx, string args)
    {
        if (ctx == IntPtr.Zero) return -1;
        GstPlayerOperation op = GstCommandTranslator.Translate(args);
        try
        {
            switch (op)
            {
                case GstLoadFileOperation load:
                    long loadStarted = Stopwatch.GetTimestamp();
                    int rc = _state.Native.Load(ctx, load.Path, load.StartSeconds ?? -1.0, _state.IsPaused, out string error);
                    // S4 計測: shim 呼び出し 1 回の実時間。shim 側 [tcs-gst] load.attempt の
                    // フェーズ内訳（preroll / first frame 等）と突き合わせて支配側を判定する。
                    Log.Information(
                        "Gst loadfile path={Path} start={Start} paused={Paused} rc={Rc} elapsedMs={ElapsedMs:F1}",
                        load.Path, load.StartSeconds, _state.IsPaused, rc,
                        (Stopwatch.GetTimestamp() - loadStarted) * 1000.0 / Stopwatch.Frequency);
                    if (rc != 0)
                        Log.Warning("GstMpvApiAdapter: loadfile 失敗 path={Path} err={Error}", load.Path, error);
                    return rc;
                case GstSeekOperation seek:
                    double seconds = seek.Seconds;
                    if (seek.Relative && _state.Native.TryGetTimePos(ctx, out double current))
                        seconds = current + seek.Seconds;
                    _state.Native.Seek(ctx, Math.Max(seconds, 0.0));
                    return 0;
                case GstStopOperation:
                    return _state.Native.Stop(ctx);
                case GstFrameStepOperation:
                    _state.Native.StepFrame(ctx);
                    return 0;
                default:
                    Log.Debug("GstMpvApiAdapter: 未対応コマンドを無視 {Command}", args);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstMpvApiAdapter.CommandString 失敗 {Command}", args);
            return -1;
        }
    }

    public void Free(IntPtr data)
    {
        // mpv_free 相当は使わない（文字列はマネージ側でコピーして返す）。
    }
}
