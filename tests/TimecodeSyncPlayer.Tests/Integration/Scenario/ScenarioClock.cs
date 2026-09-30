using System.Diagnostics;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C1: 決定的なシナリオ層の仮想時計（設計: docs/design/v0.5.4-scenario-layer.md §2-1）。
/// UTC・単調ミリ秒・QPC を 1:1 で同じだけ進める。ミリ秒の整数倍だけを受け付け、
/// 3 つの時間が食い違わないことを保証する。
/// </summary>
internal sealed class ScenarioClock : TimeProvider
{
    private DateTimeOffset _utcNow;
    private readonly List<(long AtMilliseconds, Action Action)> _scheduled = [];

    public ScenarioClock(
        DateTimeOffset utcNow,
        long monotonicMilliseconds = 10_000,
        long qpcBase = 1_000_000)
    {
        if (monotonicMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds));

        _utcNow = utcNow.ToUniversalTime();
        MonotonicMilliseconds = monotonicMilliseconds;
        QpcBase = qpcBase;
    }

    /// <summary>単調ミリ秒。LTC の損失判定と Tick が使う軸（harness の従来値と同じ 10_000 起点）。</summary>
    public long MonotonicMilliseconds { get; private set; }

    /// <summary>QPC の基準値。</summary>
    public long QpcBase { get; }

    /// <summary>サンプル時計（LtcSyncController の getQpc）が読む現在値。</summary>
    public long Qpc => QpcBase + (MonotonicMilliseconds * Stopwatch.Frequency) / 1000;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <summary>C2: 進んだ後に呼ばれる（偽プレイヤーなどの予約コールバック）。</summary>
    public event Action<TimeSpan>? Advanced;

    /// <summary>3 つの時間を同じだけ進める。ミリ秒未満の端数は受け付けない。</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "ScenarioClock は戻らない");
        if (delta.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(delta), "ScenarioClock はミリ秒単位でだけ進める");

        _utcNow = _utcNow.Add(delta);
        MonotonicMilliseconds += delta.Ticks / TimeSpan.TicksPerMillisecond;
        Advanced?.Invoke(delta);
        RunScheduled();
    }

    public void AdvanceMilliseconds(long milliseconds) =>
        Advance(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>
    /// 仮想時刻での予約（C5: 長さの更新が後から届く順序の固定など）。同時刻の予約は足した順。
    /// </summary>
    public void Schedule(long atMilliseconds, Action action)
    {
        if (atMilliseconds < MonotonicMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(atMilliseconds), "過去には予約できない");
        _scheduled.Add((atMilliseconds, action));
    }

    private void RunScheduled()
    {
        while (true)
        {
            int dueIndex = -1;
            for (int i = 0; i < _scheduled.Count; i++)
            {
                if (_scheduled[i].AtMilliseconds <= MonotonicMilliseconds)
                {
                    dueIndex = i;
                    break;
                }
            }

            if (dueIndex < 0)
                return;

            (long _, Action action) = _scheduled[dueIndex];
            _scheduled.RemoveAt(dueIndex);
            action();
        }
    }
}
