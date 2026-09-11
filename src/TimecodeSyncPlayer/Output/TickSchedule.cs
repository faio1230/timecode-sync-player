namespace TimecodeSyncPlayer.Output;

/// <summary>
/// GPU と Spout のスケジュールが共有する位相オフセット（合成位相の vblank 整列）。
/// GPU worker が書き、両 worker が読む。
/// </summary>
internal sealed class ScheduleOffset
{
    private long ticks;
    public long Ticks => Volatile.Read(ref ticks);
    public void Add(long delta) => Interlocked.Add(ref ticks, delta);
}

/// <summary>
/// Tick i は origin + offset + round(i * period) に予定する。offset は DueQpc がサンプルし
/// Take が同じサンプルを使うため、待機中に動いても期限に達した tick が「未到来」にならない。
/// 予定時刻は Take をまたいで単調増加で、過去へ大きく動いた場合は該当 index を飛ばす。
/// 試作 scripts/GpuOutputProbe の TickSchedule を移植。
/// </summary>
internal sealed class TickSchedule(long origin, double fps, long frequency, ScheduleOffset? offset = null)
{
    private long nextIndex, sampledOffset, lastScheduled = long.MinValue;
    private long At(long index) => origin + sampledOffset + (long)Math.Round(index * frequency / fps);
    private long First() { long index = nextIndex; while (At(index) <= lastScheduled) index++; return index; }
    public long DueQpc { get { sampledOffset = offset?.Ticks ?? 0; return At(First()); } }

    public (long Scheduled, long Skipped) Take(long now)
    {
        if (now < At(First())) throw new InvalidOperationException("Tick is not due.");
        long current = Math.Max(First(), (long)Math.Floor((now - origin - sampledOffset) * fps / frequency));
        long skipped = current - nextIndex;
        long scheduled = At(current);
        nextIndex = current + 1; lastScheduled = scheduled;
        return (scheduled, skipped);
    }
}

/// <summary>
/// ループ空き時間の待ち方。残りが 50 µs 超なら timer、以下なら Thread.Yield()。
/// 試作の LoopIdleWait を移植。
/// </summary>
internal static class LoopIdleWait
{
    public const long YieldMicroseconds = 50;
    public static bool UseTimer(long nowQpc, long dueQpc, long frequency) =>
        (dueQpc - nowQpc) * 1_000_000 > YieldMicroseconds * frequency;
}
