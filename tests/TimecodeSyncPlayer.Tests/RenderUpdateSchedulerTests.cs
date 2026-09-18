using System.Threading;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderUpdateSchedulerTests
{
    [Fact]
    public void RequestDispatch_SchedulesOnlyFirstCallbackUntilDispatchCompletes()
    {
        var scheduler = new RenderUpdateScheduler();

        scheduler.RequestDispatch().Should().BeTrue();
        scheduler.RequestDispatch().Should().BeFalse();
        scheduler.RequestDispatch().Should().BeFalse();

        RenderUpdateSchedulerStats stats = scheduler.ConsumeStats();
        stats.Requests.Should().Be(3);
        stats.CoalescedRequests.Should().Be(2);
    }

    [Fact]
    public void CompleteDispatch_RequestsAnotherDispatchWhenCallbacksArrivedWhileScheduled()
    {
        var scheduler = new RenderUpdateScheduler();

        scheduler.RequestDispatch().Should().BeTrue();
        scheduler.RequestDispatch().Should().BeFalse();

        scheduler.CompleteDispatch().Should().BeTrue();
        scheduler.CompleteDispatch().Should().BeFalse();
    }

    [Fact]
    public void CompleteDispatch_AllowsFutureCallbackToScheduleAfterNoPendingWork()
    {
        var scheduler = new RenderUpdateScheduler();

        scheduler.RequestDispatch().Should().BeTrue();
        scheduler.CompleteDispatch().Should().BeFalse();

        scheduler.RequestDispatch().Should().BeTrue();
    }

    [Fact]
    public void Reset_DoesNotReleaseDispatchThatIsStillRunning()
    {
        var scheduler = new RenderUpdateScheduler();

        scheduler.RequestDispatch().Should().BeTrue();

        scheduler.Reset();

        scheduler.RequestDispatch().Should().BeFalse();
        scheduler.CompleteDispatch().Should().BeTrue();
        scheduler.CompleteDispatch().Should().BeFalse();
    }

    [Fact]
    public void Reset_PreservesPendingCallbackForRunningDispatch()
    {
        var scheduler = new RenderUpdateScheduler();
        scheduler.RequestDispatch().Should().BeTrue();
        scheduler.RequestDispatch().Should().BeFalse();

        scheduler.Reset();

        scheduler.CompleteDispatch().Should().BeTrue();
    }

    [Fact]
    public void CompleteDispatch_TakesScheduledAndPendingRequestInOneStep()
    {
        var scheduler = new RenderUpdateScheduler();
        scheduler.RequestDispatch().Should().BeTrue();
        scheduler.RequestDispatch().Should().BeFalse();
        scheduler.HasPendingReschedule.Should().BeTrue("合流した要求が引き取り待ちになっている");

        scheduler.CompleteDispatch().Should().BeTrue();

        scheduler.HasPendingReschedule.Should().BeFalse("予定と要求を同じ操作で引き取る");
    }

    /// <summary>
    /// 要求を立てる側（ネイティブ）と消す側（UI）を同時に走らせ、要求が 1 回も
    /// 消えないことを固定する。drain が尽きた時点で合流要求が残っていないこと、
    /// 要求数が「予約になった数 + 合流した数」に一致することを見る。
    /// 旧実装（別フィールドの読んで消す）は、drain 後に余分な予約が残る（CompleteDispatch
    /// が true を返す）形でこのテストに落ちる。
    /// </summary>
    [Fact]
    public void ConcurrentRequests_AreNotLost()
    {
        const int iterations = 100_000;
        for (int round = 0; round < 3; round++)
        {
            foreach (int producerCount in new[] { 1, 2, 4, 8 })
                RunOneContentionRound(round, producerCount);
        }

        void RunOneContentionRound(int round, int producerCount)
        {
            var scheduler = new RenderUpdateScheduler();
            using var start = new ManualResetEventSlim(false);
            int pendingCompletions = 0;
            int scheduledByProducer = 0;
            var producers = new Thread[producerCount];
            for (int index = 0; index < producers.Length; index++)
            {
                producers[index] = new Thread(() =>
                {
                    start.Wait();
                    for (int loop = 0; loop < iterations; loop++)
                    {
                        if (scheduler.RequestDispatch())
                        {
                            Interlocked.Increment(ref scheduledByProducer);
                            Interlocked.Increment(ref pendingCompletions);
                        }
                    }
                }) { IsBackground = true };
                producers[index].Start();
            }

            start.Set();
            while (producers.Any(thread => thread.IsAlive))
                CompleteOneIfPending();
            foreach (Thread thread in producers)
                thread.Join();
            while (Volatile.Read(ref pendingCompletions) > 0)
                CompleteOneIfPending();

            void CompleteOneIfPending()
            {
                if (Volatile.Read(ref pendingCompletions) <= 0)
                {
                    Thread.SpinWait(64);
                    return;
                }

                Interlocked.Decrement(ref pendingCompletions);
                if (scheduler.CompleteDispatch())
                    Interlocked.Increment(ref pendingCompletions);
            }

            string context = $"round {round} producers {producerCount}";
            bool leftoverRestart = scheduler.CompleteDispatch();
            (leftoverRestart || scheduler.HasPendingReschedule).Should().BeFalse(
                $"{context}: 引き取り手のいない合流要求を残さない");
            RenderUpdateSchedulerStats stats = scheduler.ConsumeStats();
            stats.Requests.Should().Be(scheduledByProducer + stats.CoalescedRequests, context);
            stats.MissedReschedules.Should().Be(0, context);
            scheduler.RequestDispatch().Should().BeTrue($"{context}: drain 後の最初の要求は新しい予約になる");
        }
    }
}
