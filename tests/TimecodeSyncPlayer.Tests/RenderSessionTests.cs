using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public sealed class RenderSessionTests
{
    [Fact]
    public Task Shutdown_ContextFreeFailureRetainsNativeDependenciesAndStillDisposesIndependentResources() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        var failure = new InvalidOperationException("native free");
        fixture.Api.FreeFailure = failure;
        var calls = new List<string>();
        var disposer = new MainWindowResourceDisposer(
            () => calls.Add("timer"), fixture.Session.FreeContext, () => calls.Add("mpv"),
            () => calls.Add("ltc"), () => calls.Add("spout"), () => calls.Add("timeline"),
            () => { calls.Add("buffers"); fixture.Session.Dispose(); },
            stopRender: fixture.Session.Stop);
        try
        {
            var error = Assert.Throws<AggregateException>(disposer.DisposeAll);
            error.InnerExceptions.Should().ContainSingle().Which.Should().BeSameAs(failure);
            calls.Should().Equal("timer", "ltc", "spout", "timeline");
            fixture.Buffers.PixelPtr.Should().NotBe(IntPtr.Zero);
            fixture.Buffers.FormatStringPtr.Should().NotBe(IntPtr.Zero);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
            callback!(IntPtr.Zero);
            fixture.Scheduled.Should().BeEmpty();
            Assert.Throws<AggregateException>(fixture.Session.Dispose).Flatten().InnerExceptions.Should().Contain(failure);
            fixture.Buffers.PixelPtr.Should().NotBe(IntPtr.Zero);
        }
        finally { fixture.Api.FreeFailure = null; }
        fixture.Session.Dispose();
        fixture.Session.Dispose();
        fixture.Buffers.PixelPtr.Should().Be(IntPtr.Zero);
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
            fixture.Api.CreateReturnCode = -1;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Dispose_WaitsForRawWorkerBeforeFreeWithoutWaitingForUiContinuation(bool workerFails) => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.RenderRelease = release;
        var failure = new InvalidOperationException("native render");
        if (workerFails) fixture.Api.RenderFailure = failure;
        Task rendering = fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        await fixture.Api.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var observation = Task.Run(() =>
        {
            try
            {
                SpinWait.SpinUntil(() => !fixture.Session.IsCurrent(fixture.Session.CaptureGeneration()), TimeSpan.FromSeconds(5)).Should().BeTrue();
                fixture.Api.Calls.Should().NotContain(c => c.Operation == "free");
                fixture.Buffers.PixelPtr.Should().NotBe(IntPtr.Zero);
            }
            finally { release.Set(); }
        });
        fixture.Session.Dispose(); // UI thread: only the raw native Task may be waited here.
        await observation;
        if (workerFails)
            (await Assert.ThrowsAsync<InvalidOperationException>(() => rendering)).Should().BeSameAs(failure);
        else
            await rendering;
        fixture.Api.Calls.Select(c => c.Operation).Should().Equal("create", "callback", "render", "render-finished", "free");
        fixture.Spout.Frames.Should().BeEmpty();
        fixture.Buffers.PixelPtr.Should().Be(IntPtr.Zero);
    });

    [Fact]
    public Task QueuedCallbackAndGapFrame_AreHarmlessAfterDispose() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        fixture.Scheduled.Should().ContainSingle();
        fixture.Session.Dispose();
        fixture.Scheduled[0]();
        callback(IntPtr.Zero);
        fixture.State = GapState.BlackFrameActive;
        await fixture.Session.RenderGapAsync(GapRenderFrameDecision.Black);
        fixture.Api.Calls.Select(c => c.Operation).Should().Equal("create", "callback", "free");
        fixture.Spout.Frames.Should().BeEmpty();
        fixture.Scheduled.Should().ContainSingle();
    });

    [Fact]
    public Task NativeLifecycle_UsesOneThreadAndPublishesOnUiThread() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        int uiThread = Environment.CurrentManagedThreadId;
        await fixture.Session.ProcessUpdateAsync(async (generation, hasFrame) =>
        {
            hasFrame.Should().BeTrue();
            await fixture.Session.RenderFrameAsync(generation);
        });
        fixture.Session.Dispose();
        fixture.Api.Calls.Select(c => c.Operation).Should().Equal("create", "callback", "update", "render", "free");
        fixture.Api.Calls.Select(c => c.Thread).Distinct().Should().ContainSingle().Which.Should().NotBe(uiThread);
        fixture.Spout.Frames.Should().ContainSingle().Which.Thread.Should().Be(uiThread);
        fixture.Spout.Frames[0].Pixel.Should().Be(73);
    });

    [Fact]
    public Task InvalidateDuringNativeRender_DiscardsOldFrame() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.RenderRelease = release;
        Task rendering = fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        try
        {
            await fixture.Api.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Session.Invalidate();
        }
        finally { release.Set(); }
        await rendering;
        fixture.Spout.Frames.Should().BeEmpty();
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
    public Task GapEnteredDuringRender_SuppressesNormalFrameAndSerializesBlack() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.RenderRelease = release;
        Task rendering = fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        Task black;
        try
        {
            await fixture.Api.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.State = GapState.BlackFrameActive;
            black = fixture.Session.RenderGapAsync(GapRenderFrameDecision.Black);
            black.IsCompleted.Should().BeFalse();
            fixture.Spout.Frames.Should().BeEmpty();
        }
        finally { release.Set(); }
        await Task.WhenAll(rendering, black);
        fixture.Spout.Frames.Should().ContainSingle().Which.Pixel.Should().Be(0);
    });

    [Fact]
    public Task GapExitedWhileBlackQueued_DiscardsStaleBlack() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.RenderRelease = release;
        Task rendering = fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        Task black;
        try
        {
            await fixture.Api.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.State = GapState.BlackFrameActive;
            black = fixture.Session.RenderGapAsync(GapRenderFrameDecision.Black);
            fixture.State = GapState.Inactive;
        }
        finally { release.Set(); }
        await Task.WhenAll(rendering, black);
        fixture.Spout.Frames.Should().ContainSingle().Which.Pixel.Should().Be(73);
    });

    [Fact]
    public Task Callback_IsRetainedUntilFreeAndDisabledAfterStop() => OnUi(() =>
    {
        using var fixture = new Fixture();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        fixture.Scheduled.Should().HaveCount(1);
        fixture.Session.Stop();
        callback(IntPtr.Zero);
        fixture.Scheduled.Should().HaveCount(1);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RenderedFinalFrame_IsCapturedWithoutPublishingAndReusedForFreeze() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.State = GapState.WaitingForFrameStep;
        await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration(), fixture.Session.CaptureGapFreezeFrame);
        fixture.Spout.Frames.Should().BeEmpty();
        fixture.State = GapState.FreezeComplete;
        await fixture.Session.RenderGapAsync(GapRenderFrameDecision.GapFreeze);
        fixture.Spout.Frames.Should().ContainSingle().Which.Pixel.Should().Be(73);
    });

    [Fact]
    public Task ConcurrentNormalFrames_ArePublishedBeforeNextNativeRender() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.RenderRelease = release;
        Task first = fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        Task second;
        try
        {
            await fixture.Api.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
            second.IsCompleted.Should().BeFalse();
            fixture.Api.Calls.Count(c => c.Operation == "render").Should().Be(1);
        }
        finally { release.Set(); }
        await Task.WhenAll(first, second);
        fixture.Spout.Frames.Select(f => f.Pixel).Should().Equal((byte)73, (byte)74);
    });

    [Fact]
    public Task Callback_DispatchesFrameUpdateOnUiAndCoalescesRequests() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        int uiThread = Environment.CurrentManagedThreadId;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.FrameUpdate = async (generation, hasFrame) =>
        {
            await fixture.Session.RenderFrameAsync(generation);
            Environment.CurrentManagedThreadId.Should().Be(uiThread);
            completion.TrySetResult();
        };
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        callback(IntPtr.Zero);
        fixture.Scheduled.Should().ContainSingle();
        fixture.Scheduled[0]();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Spout.Frames.Should().ContainSingle();
        fixture.Session.ConsumeUpdateStats().CoalescedRequests.Should().Be(1);
    });

    private sealed class Fixture : IDisposable
    {
        public readonly FakeApi Api = new();
        public readonly FakeSpout Spout = new();
        public readonly List<Action> Scheduled = [];
        public GapState State = GapState.Inactive;
        public RenderSession Session { get; }
        public PixelBufferManager Buffers => (PixelBufferManager)typeof(RenderSession)
            .GetField("_buffers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Session)!;
        public Fixture(bool initialize = true)
        {
            Session = new RenderSession(Api, Spout, new PlaybackPerformanceStats(TimeSpan.FromSeconds(2)),
                () => State, () => GapBehavior.Freeze, Scheduled.Add);
            if (!initialize) return;
            Session.Create(new IntPtr(1)).Should().BeTrue();
            Session.AllocateParameters();
            Session.InitializeFrameRenderer();
            Session.InitializeStartupBuffer();
            Session.Width = 2;
            Session.Height = 2;
        }
        public void Dispose() => Session.Dispose();
    }

    private sealed class FakeSpout : ISpoutOutput
    {
        public List<(byte Pixel, int Thread)> Frames { get; } = [];
        public bool IsEnabled { get; set; } = true;
        public bool IsAvailable => true;
        public bool TryInitialize() => true;
        public void SendFrame(IntPtr pixels, int width, int height) => Frames.Add((Marshal.ReadByte(pixels), Environment.CurrentManagedThreadId));
        public void Dispose() { }
    }

    private sealed class FakeApi : IMpvRenderApi
    {
        public readonly ConcurrentQueue<(string Operation, int Thread)> Calls = new();
        public WeakReference<MpvRenderNative.MpvRenderUpdateFn>? Callback;
        public readonly TaskCompletionSource RenderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource UpdateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte _nextPixel = 73;
        public ManualResetEventSlim? RenderRelease;
        public ManualResetEventSlim? UpdateRelease;
        public Exception? FreeFailure;
        public Exception? CallbackFailure;
        public Exception? RenderFailure;
        public int CreateReturnCode;
        public int MpvRenderParamApiType => 1;
        public int MpvRenderParamSwSize => 17;
        public int MpvRenderParamSwFormat => 18;
        public int MpvRenderParamSwStride => 19;
        public int MpvRenderParamSwPointer => 20;
        public string MpvRenderApiTypeSw => "sw";
        public ulong MpvRenderUpdateFrame => 1;
        private void Record(string name) => Calls.Enqueue((name, Environment.CurrentManagedThreadId));
        public int RenderContextCreate(out IntPtr res, IntPtr mpv, MpvRenderNative.MpvRenderParam[] parameters)
        { Record("create"); res = new IntPtr(2); return CreateReturnCode; }
        public ulong RenderContextUpdate(IntPtr ctx)
        { Record("update"); UpdateStarted.TrySetResult(); UpdateRelease?.Wait(); return 1; }
        public int RenderContextRender(IntPtr ctx, MpvRenderNative.MpvRenderParam[] parameters)
        {
            Record("render"); RenderStarted.TrySetResult(); RenderRelease?.Wait();
            Marshal.WriteByte(parameters.Single(p => p.Type == MpvRenderParamSwPointer).Data, _nextPixel++);
            if (RenderRelease != null) Record("render-finished");
            if (RenderFailure != null) throw RenderFailure;
            return 0;
        }
        public void RenderContextSetUpdateCallback(IntPtr ctx, MpvRenderNative.MpvRenderUpdateFn callback, IntPtr callbackCtx)
        { Record("callback"); Callback = new(callback); if (CallbackFailure != null) throw CallbackFailure; }
        public void RenderContextFree(IntPtr ctx)
        { Record("free"); if (FreeFailure != null) throw FreeFailure; }
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
}
