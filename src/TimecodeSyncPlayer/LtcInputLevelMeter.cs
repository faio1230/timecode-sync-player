using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 R-13: LTC の入力のレベルの間引き。音声のコールバックごとに <see cref="Observe"/> を呼び、
/// <see cref="ReportInterval"/> に 1 回だけ <see cref="LtcInputLevel"/> を返す（それ以外は null）。
/// ピークは前回の知らせからの最大、フレームの数は直近 <see cref="FrameCountWindow"/> に受けた数。
/// 音声のスレッドから呼ぶので、ロックも確保もしない（知らせの record 1 個だけ）。時刻は呼び出し側の刻み
/// （本番は <see cref="System.Diagnostics.Stopwatch"/> の QPC）で渡す。LTC のデコードと同期には触らない。
/// </summary>
internal sealed class LtcInputLevelMeter
{
    /// <summary>知らせの間隔。</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>フレームを数える窓。</summary>
    public static readonly TimeSpan FrameCountWindow = TimeSpan.FromSeconds(1);

    /// <summary>ピークが 0（または数でない）ときの dBFS。</summary>
    public const double FloorDbfs = -120.0;

    // 1 秒に受けるフレームは多くて 30。余裕を見て 128 で回す（あふれたら古いものから上書き）。
    private const int FrameRingCapacity = 128;

    private readonly long _intervalTicks;
    private readonly long _windowTicks;
    private readonly long[] _frameTicks = new long[FrameRingCapacity];
    private int _frameHead;
    private int _frameCount;
    private float _peakSinceReport;
    private bool _started;
    private long _lastReportTicks;

    public LtcInputLevelMeter(long ticksPerSecond)
    {
        if (ticksPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
        _intervalTicks = (long)Math.Round(ticksPerSecond * ReportInterval.TotalSeconds);
        _windowTicks = (long)Math.Round(ticksPerSecond * FrameCountWindow.TotalSeconds);
    }

    /// <summary>
    /// 1 回のコールバックを積む。<paramref name="peak"/> はそのコールバックのピーク（線形、1.0 = 0 dBFS）、
    /// <paramref name="decodedFrames"/> はそのコールバックでデコーダが出したフレームの数。
    /// 最初の呼び出しから <see cref="ReportInterval"/> が経つごとに知らせを返す。
    /// </summary>
    public LtcInputLevel? Observe(long nowTicks, float peak, int decodedFrames)
    {
        if (!_started)
        {
            _started = true;
            _lastReportTicks = nowTicks;
        }

        if (float.IsFinite(peak) && peak > _peakSinceReport)
            _peakSinceReport = peak;

        for (int i = 0; i < decodedFrames; i++)
            PushFrame(nowTicks);

        if (nowTicks - _lastReportTicks < _intervalTicks)
            return null;

        DropFramesOlderThan(nowTicks - _windowTicks);
        var level = new LtcInputLevel(ToDbfs(_peakSinceReport), _frameCount);
        _peakSinceReport = 0f;
        _lastReportTicks = nowTicks;
        return level;
    }

    /// <summary>線形のピークを dBFS にする（0 以下・数でないものは <see cref="FloorDbfs"/>）。</summary>
    public static double ToDbfs(float peak)
    {
        if (!float.IsFinite(peak) || peak <= 0f)
            return FloorDbfs;
        return Math.Max(FloorDbfs, 20.0 * Math.Log10(peak));
    }

    private void PushFrame(long ticks)
    {
        int tail = (_frameHead + _frameCount) % FrameRingCapacity;
        _frameTicks[tail] = ticks;
        if (_frameCount < FrameRingCapacity)
            _frameCount++;
        else
            _frameHead = (_frameHead + 1) % FrameRingCapacity;
    }

    // 窓の端ちょうどのフレームは外す（直近 1 秒 = 今から 1 秒前より後）。
    private void DropFramesOlderThan(long cutoffTicks)
    {
        while (_frameCount > 0 && _frameTicks[_frameHead] <= cutoffTicks)
        {
            _frameHead = (_frameHead + 1) % FrameRingCapacity;
            _frameCount--;
        }
    }
}
