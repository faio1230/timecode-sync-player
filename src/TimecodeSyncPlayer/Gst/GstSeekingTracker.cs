using Serilog;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// 「シーク発行後、新位置のフレームが 1 枚届くまで」の判定。
/// 基準はシーク前の配信到着数（on_new_sample 到着数）で、配信イベントは消費しない。
/// 文字列経路と型付き経路が同じ状態を見るよう GstBackendState が 1 つ所有する。
/// </summary>
internal sealed class GstSeekingTracker
{
    private readonly GstBackendState _state;
    private readonly object _gate = new();
    private bool _pending;
    private ulong _arrivalBaseline;

    public GstSeekingTracker(GstBackendState state)
    {
        _state = state;
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
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending = false;
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
