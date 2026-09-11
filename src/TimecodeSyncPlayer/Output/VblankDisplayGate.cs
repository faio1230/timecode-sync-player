using System.Runtime.ExceptionServices;

namespace TimecodeSyncPlayer.Output;

internal enum VblankStep { Stop, Compose, Bootstrap, Idle, WaitTarget, Present }
internal readonly record struct VblankPrediction(long TargetQpc, long VblankQpc, long PredictedRefresh, long PeriodTicks);
internal readonly record struct VblankWaitAttempt(long StartQpc, long EndQpc, long DeadlineQpc, string Kind, string Outcome, long RequestedMicroseconds, long LatenessMicroseconds);

/// <summary>
/// vblank 位相表示の純粋な判断ロジック（単一の GPU worker が所有）。
/// 位相は present.scanout の観測（SyncQPCTime, SyncRefreshCount）から取り、次の vblank は
/// lastSyncQpc + k*period、margin 前に最新画像を出す。統計取得前は bootstrap。
/// 停止が最優先。目標を過ぎても vblank が Lead 以上先なら、期限の来た合成より先に表示する。
/// 予測付き試行の期限は予測 vblank（PresentDeadline）。表示の重複は予測 vblank 時刻で判定する。
/// lease・mutex・フェンス待ちは保持しない。試作 scripts/GpuOutputProbe の VblankDisplayGate を移植。
/// </summary>
internal sealed class VblankDisplayGate
{
    private const int PeriodSamples = 8;
    // 4K 実測の描画+Present p99 ≈ 0.4 ms から定めた定数。
    private const double LeadMs = 1;
    private readonly long frequency, marginTicks, leadTicks;
    private readonly List<long> periods = new();
    private long lastSyncQpc; private uint lastSyncRefresh;
    private VblankPrediction? pending, attempt;
    private bool armed;
    private long attemptedSlot = long.MinValue;
    public long PeriodTicks { get; private set; }
    public long MarginTicks => marginTicks;
    public long LeadTicks => leadTicks;
    public bool HasScanout { get; private set; }
    public long LastPresentedId { get; private set; }
    public long LastPresentedVblankQpc { get; private set; } = long.MinValue;
    public VblankPrediction? AttemptPrediction => attempt;
    public VblankPrediction? Pending => pending;

    public VblankDisplayGate(double marginMs, double refreshHz, long frequency)
    {
        if (frequency <= 0 || !double.IsFinite(marginMs) || marginMs <= 0) throw new ArgumentOutOfRangeException();
        this.frequency = frequency;
        marginTicks = Math.Max(1, (long)Math.Round(marginMs * frequency / 1000));
        leadTicks = Math.Max(1, (long)Math.Round(LeadMs * frequency / 1000));
        PeriodTicks = Math.Max(1, (long)Math.Round(frequency / (double.IsFinite(refreshHz) && refreshHz > 0 ? refreshHz : 60)));
    }

    // present.scanout 1件。refresh と時刻が進まない観測は無視。周期は直近8件の per-refresh 差の中央値。
    public void ObserveScanout(long syncQpc, uint syncRefresh)
    {
        if (HasScanout)
        {
            if (syncRefresh <= lastSyncRefresh || syncQpc <= lastSyncQpc) return;
            long perRefresh = (syncQpc - lastSyncQpc) / (syncRefresh - lastSyncRefresh);
            if (perRefresh <= 0) return;
            if (periods.Count == PeriodSamples) periods.RemoveAt(0);
            periods.Add(perRefresh);
            var sorted = periods.Order().ToArray();
            PeriodTicks = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }
        lastSyncQpc = syncQpc; lastSyncRefresh = syncRefresh; HasScanout = true;
    }

    // lastSyncQpc + k*period - lead > now を満たす最小の k。表示に lead 分の余裕を残して
    // 間に合う直近の vblank を予測する（目標 margin を過ぎていても、まだ lead 以上先なら候補にする）。
    public VblankPrediction Predict(long now)
    {
        if (!HasScanout) throw new InvalidOperationException("No scanout statistics yet.");
        long delta = now + leadTicks - lastSyncQpc;
        long k = delta < 0 ? 1 : delta / PeriodTicks + 1;
        long vblank = lastSyncQpc + k * PeriodTicks;
        return new(vblank - marginTicks, vblank, lastSyncRefresh + k, PeriodTicks);
    }

    // 最後に表示した vblank の半周期以内は同じ vblank とみなす。
    public bool Presentable(VblankPrediction p) => p.VblankQpc > LastPresentedVblankQpc + PeriodTicks / 2;

    // 予測付き試行の描画/Present 期限（bootstrap は合成 tick）、共通終了で上限を切る。
    public long PresentDeadline(long nextScheduled, long commonEnd) => Math.Min(attempt?.VblankQpc ?? nextScheduled, commonEnd);

    // slot: 直前の合成 tick の scheduledQpc。composeDue: 次の合成期限（または共通終了）。
    public (VblankStep Step, long DeadlineQpc, string Kind) Decide(long slot, long now, long composeDue, long latestId, bool cancelled)
    {
        armed = false;
        if (cancelled) return (VblankStep.Stop, 0, "");
        bool newer = latestId > LastPresentedId && slot != attemptedSlot;
        // 目標を過ぎても vblank がまだ届くなら、期限の来た合成より先に表示する（合成が先だとこの vblank の画像が
        // 新しい画像に置き換わり、飛び・重複になる）。
        if (pending is { } p && now >= p.TargetQpc && newer)
        {
            if (now <= p.VblankQpc - leadTicks && Presentable(p)) { attempt = p; armed = true; return (VblankStep.Present, p.VblankQpc, ""); }
            pending = null;
        }
        if (now >= composeDue) return (VblankStep.Compose, 0, "");
        if (!newer) return (VblankStep.Idle, 0, "");
        if (!HasScanout) { attempt = null; armed = true; return (VblankStep.Bootstrap, 0, ""); }
        var next = Predict(now);
        if (!Presentable(next)) return (VblankStep.Idle, 0, "");
        // 目標を過ぎていても vblank が lead 以上先なら即時 Present（pending の有無に依らない）。
        // 合成完了から判定までの遅れで目標を跨いでも、この vblank の画像を表示できる。
        if (now > next.TargetQpc && now <= next.VblankQpc - leadTicks)
        {
            attempt = next; armed = true;
            return (VblankStep.Present, next.VblankQpc, "");
        }
        pending = next;
        return next.TargetQpc < composeDue ? (VblankStep.WaitTarget, next.TargetQpc, "target") : (VblankStep.WaitTarget, composeDue, "compose");
    }

    public bool Wait(long deadline, string kind, Func<long> timestamp, Func<long, long, VblankWaitResult> waitUntil, Action<VblankWaitAttempt> record)
    {
        if (kind is not ("target" or "compose")) throw new ArgumentException("Wait kind must be target or compose.");
        long start = timestamp();
        long requested = Math.Max(0, (deadline - start) * 1_000_000 / frequency);
        VblankWaitResult result = VblankWaitResult.Reached; Exception? error = null; long end;
        try { result = waitUntil(start, deadline); }
        catch (Exception e) { error = e; }
        finally { end = timestamp(); }
        string outcome = error != null ? "error" : result == VblankWaitResult.Cancelled ? "cancelled" : kind;
        long lateness = outcome == "target" ? (end - deadline) * 1_000_000 / frequency : 0;
        record(new(start, end, deadline, kind, outcome, requested, lateness));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result == VblankWaitResult.Reached;
    }

    public void BeginAttempt(long slot)
    {
        if (slot <= attemptedSlot) throw new InvalidOperationException("At most one display attempt per compose slot.");
        if (!armed) throw new InvalidOperationException("Display attempt requires a Bootstrap or Present decision.");
        attemptedSlot = slot; armed = false;
    }

    // Present/Bootstrap の判断が実際に試行されなかった場合（latency waitable 未シグナル）:
    // 予測を捨て、次の Decide はより後の vblank を狙う。枠の試行権は残す。
    public void Defer() { pending = null; attempt = null; armed = false; }

    public string? SkipReason(long imageId) => imageId > LastPresentedId ? null : "display.vblank.noNewerImage";

    /// <summary>D-2: vblank−lead までの待ち時間（ms、最大 maxMs）。既に過ぎていれば 0。</summary>
    public static int TimeoutUntilMs(long nowQpc, long untilQpc, long frequency, int maxMs)
    {
        if (frequency <= 0 || maxMs <= 0) return 0;
        return (int)Math.Clamp(Math.Floor((untilQpc - nowQpc) * 1000.0 / frequency), 0, maxMs);
    }

    public void Presented(long imageId)
    {
        if (imageId <= LastPresentedId) throw new InvalidOperationException("Presented image ids must strictly increase.");
        if (attempt is { } a)
        {
            if (!Presentable(a)) throw new InvalidOperationException("At most one present per predicted vblank.");
            LastPresentedVblankQpc = a.VblankQpc; pending = null;
        }
        LastPresentedId = imageId; attempt = null;
    }
}
