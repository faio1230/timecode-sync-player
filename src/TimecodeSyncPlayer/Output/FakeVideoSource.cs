using System.Collections.Concurrent;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// 契約のフェイクソース。デコードスレッドの代わりに Tick(nowQpc) で駆動する。1周期に最大1枚を
/// origin + i/fps で生成し、遅れた周期は飛ばす。PositionSeconds は予定時刻から、DecodedQpc は tick 時刻。
/// slot はここが所有し、render(slot, stamp) が空き slot を埋めてから Offer する。
/// デコーダ側 pool はリングより1枚多いため、リング満杯でも描画先が残る。試作 FakeVideoSource を移植。
/// </summary>
internal sealed class FakeVideoSource<TSlot> : IVideoSource
{
    public const double DefaultFps = 30;
    private readonly SourceImageRing<TSlot> ring;
    private readonly ConcurrentQueue<TSlot> free = new();
    private readonly TickSchedule schedule;
    private readonly long origin, frequency;
    private readonly double endPosition;
    private readonly Action<TSlot, SourceImageStamp>? render;
    private readonly string decoder, gpu;
    private long sequence, starved, produced, skipped;
    private bool endSignalled;

    public FakeVideoSource(IReadOnlyList<TSlot> slots, long originQpc, long frequency, double fps = DefaultFps, double endPositionSeconds = double.PositiveInfinity,
        Action<TSlot, SourceImageStamp>? render = null, Func<TSlot, SourceImageDescription>? describe = null, string decoder = "fake-pattern", string gpu = "none")
    {
        if (!double.IsFinite(fps) || fps <= 0 || frequency <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        ring = new SourceImageRing<TSlot>(slots.Count - 1, describe, free.Enqueue);
        foreach (var slot in slots) free.Enqueue(slot);
        schedule = new TickSchedule(originQpc, fps, frequency);
        origin = originQpc; this.frequency = frequency; endPosition = endPositionSeconds; this.render = render; this.decoder = decoder; this.gpu = gpu;
    }

    public long Produced => Volatile.Read(ref produced);
    public long Starved => Volatile.Read(ref starved);
    public long SkippedPeriods => Volatile.Read(ref skipped);
    public int FreeSlots => free.Count;

    public bool Tick(long nowQpc)
    {
        if (nowQpc < schedule.DueQpc) return false;
        var (scheduled, late) = schedule.Take(nowQpc);
        skipped += late;
        double position = (scheduled - origin) / (double)frequency;
        if (position >= endPosition) { if (!endSignalled) { ring.SignalEnd(endPosition); endSignalled = true; } return false; }
        if (!free.TryDequeue(out var slot)) { starved++; return false; }
        var stamp = new SourceImageStamp(ring.Generation, ++sequence, position, nowQpc);
        render?.Invoke(slot, stamp);
        if (!ring.Offer(stamp, slot)) { free.Enqueue(slot); return false; }
        produced++; return true;
    }

    public void SetGeneration(int generation) { ring.SetGeneration(generation); endSignalled = false; }
    public SourceStatus TryAcquire(int generation, double positionSeconds, out ISourceImageLease? lease) => ring.TryAcquire(generation, positionSeconds, out lease);
    public SourceDiagnostics Diagnostics => ring.Diagnostics(decoder, gpu, "BGRA8_UNORM");
    public bool TryDispose() => ring.TryDispose();
    public void Dispose() => ring.Dispose();
}
