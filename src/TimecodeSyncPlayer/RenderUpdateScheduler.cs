using System.Threading;

namespace TimecodeSyncPlayer;

/// <summary>
/// ネイティブ側の再スケジュール要求と UI 側の drain 完了を仲介する。
/// 「予定中」と「合流した要求」を 1 つの語のビットで持ち、要求の読み取りと消去を
/// 1 回の Interlocked にまとめる。別々のフィールドを読んで消す実装では、
/// 合流を数えてから要求を立てる側と、要求を読んでから消す側が交差したときに
/// 要求が 1 回分消える窓ができる（D36）。
/// </summary>
public sealed class RenderUpdateScheduler : IRenderUpdateScheduler
{
    private const int DispatchScheduled = 1;
    private const int RescheduleRequested = 2;

    private int _state;
    private int _requests;
    private int _coalescedRequests;
    private int _reschedules;
    private int _missedReschedules;

    public bool RequestDispatch()
    {
        Interlocked.Increment(ref _requests);
        while (true)
        {
            int state = Volatile.Read(ref _state);

            // 予約ビットだけが残った状態。旧実装の取りこぼしが残す形で、いまは起きない。
            // 退行検出用に数えてから、下の予約として引き受ける（要求は捨てない）。
            if (state == RescheduleRequested)
                Interlocked.Increment(ref _missedReschedules);

            if ((state & DispatchScheduled) == 0)
            {
                if (Interlocked.CompareExchange(ref _state, state | DispatchScheduled, state) == state)
                    return true;
                continue;
            }

            if ((state & RescheduleRequested) != 0)
            {
                Interlocked.Increment(ref _coalescedRequests);
                return false;
            }

            // 合流の印は「予定中」を確認した同じ語への CAS で立てる。予定が先に消えていれば
            // CAS が外れ、再評価で自分が予約を引き受ける。無条件の store だと、消去と
            // 交差したときに印だけが残って誰も拾わない。
            if (Interlocked.CompareExchange(ref _state, state | RescheduleRequested, state) == state)
            {
                Interlocked.Increment(ref _coalescedRequests);
                return false;
            }
        }
    }

    public bool CompleteDispatch()
    {
        // 予定ビットと要求ビットを 1 回の操作で読んで消す。前後に来た要求は、
        // この呼び出しの CAS が成功する（要求を引き取る）か、他の呼び出しの CAS が
        // 先に成功する（その呼び出しが予約を立てる）かのどちらかになる。
        int state = Interlocked.Exchange(ref _state, 0);
        if ((state & RescheduleRequested) == 0)
            return false;

        if (Interlocked.CompareExchange(ref _state, DispatchScheduled, 0) != 0)
            return false;

        Interlocked.Increment(ref _reschedules);
        return true;
    }

    public RenderUpdateSchedulerStats ConsumeStats() =>
        new(
            Interlocked.Exchange(ref _requests, 0),
            Interlocked.Exchange(ref _coalescedRequests, 0),
            Interlocked.Exchange(ref _reschedules, 0),
            Interlocked.Exchange(ref _missedReschedules, 0));

    public void Reset()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _coalescedRequests, 0);
        Interlocked.Exchange(ref _reschedules, 0);
        Interlocked.Exchange(ref _missedReschedules, 0);
    }

    /// <summary>引き取り手のいない合流要求が残っているか（健全な状態では常に false）。</summary>
    internal bool HasPendingReschedule => (Volatile.Read(ref _state) & RescheduleRequested) != 0;
}

public readonly record struct RenderUpdateSchedulerStats(
    int Requests,
    int CoalescedRequests,
    int Reschedules,
    int MissedReschedules);
