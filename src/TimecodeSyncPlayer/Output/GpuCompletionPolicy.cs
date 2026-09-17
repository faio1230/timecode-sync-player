namespace TimecodeSyncPlayer.Output;

internal enum GpuWaitDecision { Completed, Pending, DeviceLost, Stuck }

/// <summary>
/// D28: GPU 完了待ちの共通規則。100ms スライスを超えても fault にはせず、呼び出し側が
/// この tick を skip して次 tick で再試行できるようにする。Playback unavailable へ落とすのは
/// デバイス消失（GpuDeviceLostException）か、未完了が FaultAfterSeconds 秒「連続」したときだけ。
/// 完了すると連続時間は仕切り直す。
/// </summary>
internal sealed class GpuCompletionPolicy
{
    /// <summary>1 回の待ちでブロックしてよい時間。超過したら retry（skip）を検討する。</summary>
    public const int SliceMilliseconds = 100;

    /// <summary>未完了が連続してこの秒数に達したときだけ fault（Playback unavailable）へ落とす。</summary>
    public const double FaultAfterSeconds = 3.0;

    private readonly double _ticksPerSecond;
    private readonly object _gate = new();
    private long pendingSinceQpc = -1;
    private bool stuckReported;

    public GpuCompletionPolicy(long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _ticksPerSecond = frequency;
    }

    /// <summary>FaultAfterSeconds 到達を報告済みか（fault を 1 回だけ出すため）。</summary>
    public bool StuckReported { get { lock (_gate) return stuckReported; } }

    /// <summary>GPU worker と Spout worker が同じ GpuFence を共有するため、状態はロックで守る。</summary>
    public GpuWaitDecision Decide(bool completed, bool deviceRemoved, long nowQpc)
    {
        if (deviceRemoved) return GpuWaitDecision.DeviceLost;
        lock (_gate)
        {
            if (completed)
            {
                pendingSinceQpc = -1;
                stuckReported = false;
                return GpuWaitDecision.Completed;
            }

            if (pendingSinceQpc < 0) pendingSinceQpc = nowQpc;
            if ((nowQpc - pendingSinceQpc) / _ticksPerSecond >= FaultAfterSeconds)
            {
                stuckReported = true;
                return GpuWaitDecision.Stuck;
            }
            return GpuWaitDecision.Pending;
        }
    }

    public bool SliceExpired(long sliceStartQpc, long nowQpc) =>
        (nowQpc - sliceStartQpc) / _ticksPerSecond * 1000.0 >= SliceMilliseconds;
}
