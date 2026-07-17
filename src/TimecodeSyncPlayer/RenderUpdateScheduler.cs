using System.Threading;

namespace TimecodeSyncPlayer;

public sealed class RenderUpdateScheduler : IRenderUpdateScheduler
{
    private int _dispatchScheduled;
    private int _rescheduleRequested;
    private int _requests;
    private int _coalescedRequests;

    public bool RequestDispatch()
    {
        Interlocked.Increment(ref _requests);
        if (Interlocked.CompareExchange(ref _dispatchScheduled, 1, 0) == 0)
            return true;

        Interlocked.Increment(ref _coalescedRequests);
        Interlocked.Exchange(ref _rescheduleRequested, 1);
        return false;
    }

    public bool CompleteDispatch()
    {
        Interlocked.Exchange(ref _dispatchScheduled, 0);
        if (Interlocked.Exchange(ref _rescheduleRequested, 0) == 0)
            return false;

        return Interlocked.CompareExchange(ref _dispatchScheduled, 1, 0) == 0;
    }

    public RenderUpdateSchedulerStats ConsumeStats()
    {
        int requests = Interlocked.Exchange(ref _requests, 0);
        int coalescedRequests = Interlocked.Exchange(ref _coalescedRequests, 0);
        return new RenderUpdateSchedulerStats(requests, coalescedRequests);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _coalescedRequests, 0);
    }
}

public readonly record struct RenderUpdateSchedulerStats(
    int Requests,
    int CoalescedRequests);
