using Serilog;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// 「シーク発行後、新位置のフレームが 1 枚届くまで」の判定。
/// 基準はシーク前の配信到着数（on_new_sample 到着数）で、配信イベントは消費しない。
/// GstPlaybackApi と Gap 経路が同じ状態を見るよう GstBackendState が 1 つ所有する。
/// D11: 新しい配信が来ない EOF 後は (a) Ended の観測、(b) 発行から 2 秒の安全網で解除する。
/// </summary>
internal sealed class GstSeekingTracker
{
    internal static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(2);

    private readonly GstBackendState _state;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private bool _pending;
    private ulong _arrivalBaseline;
    private DateTimeOffset _pendingSinceUtc;

    public GstSeekingTracker(GstBackendState state, TimeProvider? timeProvider = null)
    {
        _state = state;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>シーク発行前に到着数の基準を取る。</summary>
    public ulong ReadArrivalBaseline(IntPtr player) => ReadDeliveryArrivals(player);

    /// <summary>シーク発行が成立したときに呼ぶ（新位置フレームが届くまで true にする）。</summary>
    public void MarkPending(ulong arrivalBaseline)
    {
        lock (_gate)
        {
            _pending = true;
            _arrivalBaseline = arrivalBaseline;
            _pendingSinceUtc = _timeProvider.GetUtcNow();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending = false;
        }
    }

    /// <summary>
    /// D11: EOF（shim の Ended / EOS）を観測したときに呼ぶ。EOF 後は新しい配信が来ないため、
    /// 到着数では pending が解除されない。スカラのフラグだけを操作する。
    /// </summary>
    public void NotifyEnded()
    {
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
            Log.Information("GstSeekingTracker: EOF（Ended）を観測したためシーク保留を解除しました");
        }
    }

    public bool IsSeeking(IntPtr player)
    {
        lock (_gate)
        {
            if (!_pending) return false;
        }
        ulong arrivals = ReadDeliveryArrivals(player);
        lock (_gate)
        {
            if (!_pending) return false;
            if (arrivals > _arrivalBaseline)
            {
                _pending = false;
                return false;
            }
            if (_timeProvider.GetUtcNow() - _pendingSinceUtc >= PendingTimeout)
            {
                // D11 の安全網: 配信が来なくてもシーク発行から 2 秒で解除する。
                _pending = false;
                Log.Information("GstSeekingTracker: シーク発行から 2 秒経過したため保留を解除しました（新位置フレーム未到着）");
                return false;
            }
            return true;
        }
    }

    private ulong ReadDeliveryArrivals(IntPtr player)
    {
        try
        {
            return _state.Native.GetDeliveryStats(player, out GstNative.TcsDeliveryStats stats) == 0
                ? stats.Arrivals
                : 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GstSeekingTracker: 配信到着数の取得に失敗");
            return 0;
        }
    }
}
