using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

/// <summary>
/// V3 計測: IsNativeSeeking() の戻り値と、その判定に使う seeking プロパティの生値を残す。
/// events.jsonl へは変化時と 2 秒ごと（連続性の証跡）、アプリログへは変化時のみ書く。
/// トレース無効時もアプリログの変化記録だけは残す（サンプリング判定には影響しない）。
/// </summary>
internal sealed class SeekingProbe
{
    private static readonly long HeartbeatTicks = 2 * Stopwatch.Frequency;
    private bool _initialized;
    private string _lastRaw = "";
    private bool _lastResult;
    private long _lastQpc;

    public void Record(string raw, bool result)
    {
        long now = Stopwatch.GetTimestamp();
        bool changed = !_initialized || raw != _lastRaw || result != _lastResult;
        if (!changed && now - _lastQpc < HeartbeatTicks) return;
        _initialized = true;
        _lastRaw = raw;
        _lastResult = result;
        _lastQpc = now;
        if (OutputTrace.Current.IsEnabled)
        {
            OutputTrace.Current.Record(new("player.seeking", "PLAYER", now,
                Value: result ? 1 : 0,
                Detail: "raw=" + (raw.Length == 0 ? "<empty>" : raw)));
        }
        if (changed)
            Log.Information("player.seeking raw={Raw} isNativeSeeking={Result}",
                raw.Length == 0 ? "<empty>" : raw, result);
    }
}
