using System.Collections.Concurrent;
using System.Windows.Threading;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 段 3 以降の RenderSession は、shim のフレーム通知の駆動・世代管理・寿命管理だけを受け持つ。
/// CPU へのフレームコピー（snapshot / 描画 / Spout 発行）は除去済み。
/// </summary>
public sealed class RenderSessionTests
{
    [Fact]
    public async Task TryCaptureGapFreezeFrameAsync_RequiresCurrentGenerationAndAttempt()
    {
        using var fixture = new Fixture();
        int generation = fixture.Session.CaptureGeneration();

        (await fixture.Session.TryCaptureGapFreezeFrameAsync(generation, () => true)).Should().BeTrue();
        (await fixture.Session.TryCaptureGapFreezeFrameAsync(generation - 1, () => true)).Should().BeFalse();
        (await fixture.Session.TryCaptureGapFreezeFrameAsync(generation, () => false)).Should().BeFalse();

        fixture.Session.Invalidate();
        (await fixture.Session.TryCaptureGapFreezeFrameAsync(generation, () => true)).Should().BeFalse();
    }

    [Fact]
    public Task Callback_IsRetainedUntilFreeAndDisabledAfterStop() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        await WaitUntil(() => fixture.Scheduled.Count == 1);
        fixture.Scheduled.Should().HaveCount(1);
        fixture.Session.Stop();
        callback(IntPtr.Zero);
        fixture.Scheduled.Should().HaveCount(1);
    });

    [Fact]
    public Task NativeCallback_DrainsUpdateWhileUiDispatchIsWithheld() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();

        callback!(IntPtr.Zero);

        await WaitUntil(() => fixture.Api.Calls.Any(c => c.Operation == "update"));
        fixture.Scheduled.Should().ContainSingle();
    });

    [Fact]
    public Task Stop_FromAnotherThreadReturnsWhileUiThreadIsBusy() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        await WaitUntil(() => fixture.Scheduled.Count == 1);

        Exception? failure = null;
        var stopper = new Thread(() =>
        {
            try { fixture.Session.Stop(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        stopper.Start();

        // この UI 開始を保持したまま Stop が戻ること（UI 継続を待たない）。
        Assert.True(stopper.Join(TimeSpan.FromSeconds(2)));
        failure.Should().BeNull();
    });

    [Fact]
    public Task QueuedCallback_IsHarmlessAfterDispose() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        await WaitUntil(() => fixture.Scheduled.Count == 1);

        fixture.Session.Dispose();
        fixture.Scheduled[0]();
        callback(IntPtr.Zero);

        fixture.Api.Calls.Select(c => c.Operation).Should().Equal("create", "callback", "update", "free");
    });

    [Fact]
    public Task NativeLifecycle_NativeCallsUseOneThreadAndFrameUpdateRunsOnUi() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        int uiThread = Environment.CurrentManagedThreadId;
        bool ran = false;

        await fixture.Session.ProcessUpdateAsync((generation, hasFrame) =>
        {
            hasFrame.Should().BeTrue();
            Environment.CurrentManagedThreadId.Should().Be(uiThread);
            ran = true;
            return Task.CompletedTask;
        });

        ran.Should().BeTrue();
        fixture.Session.Dispose();
        fixture.Api.Calls.Select(c => c.Operation).Should().Equal("create", "callback", "update", "free");
        fixture.Api.Calls.Select(c => c.Thread).Distinct().Should().ContainSingle().Which.Should().NotBe(uiThread);
    });

    [Fact]
    public Task InvalidateDuringNativeUpdate_DoesNotRunOldContinuation() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.UpdateRelease = release;
        bool continued = false;
        Task update = fixture.Session.ProcessUpdateAsync((_, _) => { continued = true; return Task.CompletedTask; });
        try
        {
            await fixture.Api.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Session.Invalidate();
        }
        finally { release.Set(); }
        await update;
        continued.Should().BeFalse();
    });

    [Fact]
    public Task Callback_DispatchesFrameUpdateOnUiAndCoalescesRequests() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        int uiThread = Environment.CurrentManagedThreadId;
        using var frameUpdateEntered = new ManualResetEventSlim();
        using var releaseFrameUpdate = new ManualResetEventSlim();
        int frameUpdateCalls = 0;
        fixture.Session.FrameUpdate = (generation, hasFrame) =>
        {
            Environment.CurrentManagedThreadId.Should().Be(uiThread);
            Interlocked.Increment(ref frameUpdateCalls);
            frameUpdateEntered.Set();
            // UI スレッドをここで保持する（2 回目の要求は保持中に入れ、解除後に数える）。
            releaseFrameUpdate.Wait();
            return Task.CompletedTask;
        };
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

        // 1 回目: ネイティブ側の drain 完了を直列 executor のバリアで待ってから UI の予約を見る。
        // （時間には頼らない。drain はバリアより前に executor へ積まれている。）
        callback!(IntPtr.Zero);
        await DrainNativeQueueAsync(fixture);
        fixture.Scheduled.Should().ContainSingle("1 回目の drain が UI へ 1 件予約する");

        // UI スレッドで予約済みの 1 件を実行開始し、FrameUpdate の中で保持する。
        // ここから先は UI スレッドが塞がるため、テスト本体はワーカー側に移って進める。
        _ = dispatcher.BeginInvoke(new Action(() => fixture.Scheduled[0]()));
        await Task.Run(async () =>
        {
            try
            {
                frameUpdateEntered.Wait(TimeSpan.FromSeconds(5))
                    .Should().BeTrue("UI スレッドで最初のフレーム更新が保持状態に入る");

                // UI を保持したままネイティブ側から 2 回目の要求を出す。UI の予約は未完了なので
                // ここで合流が 1 回計上される（drain の完了は executor のバリアで順序付ける）。
                callback(IntPtr.Zero);
                await DrainNativeQueueAsync(fixture);

                fixture.Session.ConsumeUpdateStats().CoalescedRequests.Should().Be(1);
                fixture.Scheduled.Should().ContainSingle("合流した要求は新しい予約を作らない");
            }
            finally
            {
                releaseFrameUpdate.Set();
            }
        });

        Volatile.Read(ref frameUpdateCalls).Should().BeGreaterThan(0);
    });

    /// <summary>
    /// RenderSession のネイティブ executor に空の処理を積み、それまでの drain が完了したことを
    /// 順序で保証する（時間待ちではなく、単一スレッドの FIFO を使ったバリア）。
    /// </summary>
    private static Task DrainNativeQueueAsync(Fixture fixture) =>
        fixture.Session.ProcessUpdateAsync(static (_, _) => Task.CompletedTask);

    [Fact]
    public Task Shutdown_ContextFreeFailureRetainsNativeDependencies() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        var failure = new InvalidOperationException("native free");
        fixture.Api.FreeFailure = failure;
        var calls = new List<string>();
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"), fixture.Session.FreeContext, () => calls.Add("player"),
            () => calls.Add("ltc"), () => calls.Add("spout"), () => calls.Add("timeline"),
            () => { calls.Add("buffers"); fixture.Session.Dispose(); },
            stopRender: fixture.Session.Stop);
        try
        {
            var error = Assert.Throws<AggregateException>(disposer.DisposeAll);
            error.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(failure);
            calls.Should().Equal("timer", "ltc", "spout", "timeline");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
            callback!(IntPtr.Zero);
            fixture.Scheduled.Should().BeEmpty();
            Assert.Throws<AggregateException>(fixture.Session.Dispose).Flatten().InnerExceptions.Should().Contain(failure);
        }
        finally { fixture.Api.FreeFailure = null; }
        fixture.Session.Dispose();
        fixture.Session.Dispose();
        fixture.Api.Calls.Count(c => c.Operation == "free").Should().Be(3);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task Dispose_PartialInitializationReleasesContextAtMostOnce(int phase) => OnUi(() =>
    {
        using var fixture = new Fixture(initialize: false);
        if (phase == 1)
        {
            fixture.Api.CreateSucceeds = false;
            fixture.Session.Create(new IntPtr(1)).Should().BeFalse();
        }
        if (phase == 2)
        {
            fixture.Api.CallbackFailure = new InvalidOperationException("register callback");
            Assert.Throws<InvalidOperationException>(() => fixture.Session.Create(new IntPtr(1)));
        }
        fixture.Session.Dispose();
        fixture.Session.Dispose();
        fixture.Api.Calls.Count(c => c.Operation == "free").Should().Be(phase == 0 ? 0 : 1);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session.Create(new IntPtr(1)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Dispose_WaitsForNativeUpdateWithoutWaitingForUiContinuation() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.UpdateRelease = release;
        bool continued = false;
        Task update = fixture.Session.ProcessUpdateAsync((_, _) => { continued = true; return Task.CompletedTask; });
        await fixture.Api.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var observation = Task.Run(() =>
        {
            try
            {
                SpinWait.SpinUntil(() => !fixture.Session.IsCurrent(fixture.Session.CaptureGeneration()),
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                fixture.Api.Calls.Should().NotContain(c => c.Operation == "free");
            }
            finally { release.Set(); }
        });

        fixture.Session.Dispose(); // UI スレッド: ネイティブ Task だけを待ち、UI 継続は待たない。
        await observation;
        await update;
        continued.Should().BeFalse();
        fixture.Api.Calls.Select(c => c.Operation).Should().Equal("create", "callback", "update", "free");
    });

    private sealed class Fixture : IDisposable
    {
        public readonly FakeApi Api = new();
        private readonly ConcurrentQueue<Action> _scheduled = new();
        public IReadOnlyList<Action> Scheduled => _scheduled.ToArray();
        public RenderSession Session { get; }

        public Fixture(bool initialize = true)
        {
            Session = new RenderSession(Api, new PlaybackPerformanceStats(TimeSpan.FromSeconds(2)),
                _scheduled.Enqueue);
            if (!initialize) return;
            Session.Create(new IntPtr(1)).Should().BeTrue();
        }

        public void Dispose() => Session.Dispose();
    }

    private sealed class FakeApi : IRenderUpdateSource
    {
        public readonly ConcurrentQueue<(string Operation, int Thread)> Calls = new();
        public WeakReference<RenderUpdateFn>? Callback;
        public readonly TaskCompletionSource UpdateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim? UpdateRelease;
        public Exception? FreeFailure;
        public Exception? CallbackFailure;
        public bool CreateSucceeds = true;

        public ulong FrameUpdateFlag => 1;

        private void Record(string name) => Calls.Enqueue((name, Environment.CurrentManagedThreadId));

        public bool TryCreateContext(IntPtr player, out IntPtr context)
        {
            Record("create");
            context = new IntPtr(2);
            return CreateSucceeds;
        }

        public ulong ConsumeUpdate(IntPtr ctx)
        {
            Record("update");
            UpdateStarted.TrySetResult();
            UpdateRelease?.Wait();
            return FrameUpdateFlag;
        }

        public void SetUpdateCallback(IntPtr ctx, RenderUpdateFn? callback)
        {
            Record("callback");
            Callback = new(callback!);
            if (CallbackFailure != null) throw CallbackFailure;
        }

        public void FreeContext(IntPtr ctx)
        {
            Record("free");
            if (FreeFailure != null) throw FreeFailure;
        }
    }

    private static Task OnUi(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
}
