using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public sealed class RenderSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PendingFreeze_KeepsPublishedImageInsteadOfPriorClipBuffer(bool hasOldCachedFrame) => OnUi(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        fixture.Buffers.EnsureFrozenFrameBuffer(2, 2);
        fixture.Buffers.FrozenFrameBuffer!.AsSpan().Fill(5);
        if (hasOldCachedFrame) fixture.Buffers.CopyFrozenToGapFreezeFrame(2, 2);
        fixture.State = GapState.EnteringFreeze;
        await fixture.Session.RenderGapAsync(fixture.Session.GetGapRenderDecision());
        fixture.Spout.Frames.Select(frame => frame.Pixel).Should().Equal(new byte[] { 73 },
            "pending Freeze must keep the currently published image, not another clip's frozen buffer");
    });

    [Fact]
    public Task FreezeTimeoutWithoutCachedImage_KeepsPublishedImage() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        fixture.Buffers.EnsureFrozenFrameBuffer(2, 2);
        fixture.Buffers.FrozenFrameBuffer!.AsSpan().Fill(5);
        fixture.State = GapState.FreezeComplete;
        await fixture.Session.RenderGapAsync(fixture.Session.GetGapRenderDecision());
        fixture.Spout.Frames.Select(frame => frame.Pixel).Should().Equal(new byte[] { 73 });
    });

    [Fact]
    public Task FreezeTimeoutWithUnconfirmedOldCache_KeepsPublishedImage() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        fixture.Buffers.EnsureFrozenFrameBuffer(2, 2);
        fixture.Buffers.FrozenFrameBuffer!.AsSpan().Fill(5);
        fixture.Buffers.CopyFrozenToGapFreezeFrame(2, 2);
        fixture.FreezeConfirmed = false;
        fixture.State = GapState.FreezeComplete;
        fixture.Session.GetGapRenderDecision().Should().Be(GapRenderFrameDecision.Hold);
        await fixture.Session.RenderGapAsync(GapRenderFrameDecision.Hold);
        fixture.Spout.Frames.Select(frame => frame.Pixel).Should().Equal(new byte[] { 73 });
    });

    [Fact]
    public Task PendingFreezeStillDrainsNativeFramesWhilePreservingPublishedImage() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        fixture.State = GapState.EnteringFreeze;
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        await WaitUntil(() => fixture.Api.Calls.Count(c => c.Operation == "render") == 2);
        await WaitUntil(() => fixture.Scheduled.Count == 1);
        fixture.Scheduled[0]();
        await fixture.Session.RenderGapAsync(GapRenderFrameDecision.Hold);
        fixture.Spout.Frames.Select(frame => frame.Pixel).Should().Equal(new byte[] { 73 });
    });

    [Fact]
    public Task NormalRenderCompletingDuringPendingFreeze_DoesNotCopyOrCertifyItsPixels() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        using var release = new ManualResetEventSlim();
        fixture.Api.RenderRelease = release;
        bool completed = false;
        Task rendering = fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration(), () => completed = true);
        try
        {
            await WaitUntil(() => fixture.Api.Calls.Count(c => c.Operation == "render") == 2);
            fixture.State = GapState.EnteringFreeze;
        }
        finally { release.Set(); }
        await rendering;
        completed.Should().BeFalse("an incidental normal frame is not an explicit final-frame capture");
        fixture.Buffers.PixelBuffer![0].Should().Be(73);
        fixture.Spout.Frames.Should().ContainSingle();
    });

    [Fact]
    public Task NativeCallback_DrainsFrameWhileUiDispatchIsWithheld() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        // A synchronous core property call may block this UI. Native rendering must
        // already be running without executing any of the queued UI actions.
        await fixture.Api.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Api.Calls.Should().Contain(c => c.Operation == "update");
        fixture.Spout.Frames.Should().BeEmpty();
    });

    [Fact]
    public Task ContinuousNativeCallbacks_DoNotStarveQueuedExplicitRedraw() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.Api.RepeatCallback = true;
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        await fixture.Api.RenderStarted.Task;
        try
        {
            // Metadata redraw holds the UI publication gate while waiting for this
            // explicit native job. Continuous FRAME work must yield the worker queue.
            await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration())
                .WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Spout.Frames.Should().ContainSingle();
        }
        finally { fixture.Api.RepeatCallback = false; }
    });

    [Fact]
    public Task PreparedFrame_IsStableAcrossAwaitAndNeverRendersAgainOnPublication() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.FrameUpdate = async (generation, hasFrame) =>
        {
            entered.SetResult();
            await resume.Task;
            await fixture.Session.RenderFrameAsync(generation);
            done.SetResult();
        };
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        await WaitUntil(() => fixture.Scheduled.Count == 1);
        fixture.Scheduled[0]();
        await entered.Task;
        callback(IntPtr.Zero);
        await WaitUntil(() => fixture.Api.Calls.Count(c => c.Operation == "render") == 2);
        resume.SetResult();
        await done.Task;
        fixture.Api.Calls.Count(c => c.Operation == "render").Should().Be(2);
        fixture.Spout.Frames.Should().ContainSingle().Which.Pixel.Should().Be(73);
    });

    [Fact]
    public Task OlderPreparedFrame_DoesNotOverwriteNewerExplicitRedraw() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.FrameUpdate = async (generation, hasFrame) =>
        {
            entered.SetResult();
            await resume.Task;
            await fixture.Session.RenderFrameAsync(generation);
            done.SetResult();
        };
        fixture.Api.Callback!.TryGetTarget(out var callback).Should().BeTrue();
        callback!(IntPtr.Zero);
        await WaitUntil(() => fixture.Scheduled.Count == 1);
        fixture.Scheduled[0]();
        await entered.Task;
        await fixture.Session.RenderFrameAsync(fixture.Session.CaptureGeneration());
        fixture.Spout.Frames.Should().ContainSingle().Which.Pixel.Should().Be(74);
        resume.SetResult();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Spout.Frames.Select(frame => frame.Pixel).Should().Equal(new byte[] { 74 },
            "an older callback lease must not roll back pixels already published by a newer redraw");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CaptureFreeze_RejectsFailedRenderOrInvalidSize(bool invalidSize) => OnUi(async () =>
    {
        using var fixture = new Fixture();
        if (invalidSize) fixture.Session.Width = 0;
        else fixture.Api.RenderReturnCode = -1;
        bool captured = await fixture.Session.TryCaptureGapFreezeFrameAsync(fixture.Session.CaptureGeneration(), () => true);
        captured.Should().BeFalse();
        fixture.Buffers.CachedGapFreezeFrameBuffer.Should().BeNull();
        fixture.Api.Calls.Count(c => c.Operation == "render").Should().Be(1);
    });

    [Fact]
    public Task CaptureFreeze_RejectsAttemptChangedDuringNativeRender() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.RenderRelease = release;
        bool current = true;
        Task<bool> capture = fixture.Session.TryCaptureGapFreezeFrameAsync(fixture.Session.CaptureGeneration(), () => current);
        await fixture.Api.RenderStarted.Task;
        current = false;
        release.Set();
        (await capture).Should().BeFalse();
        fixture.Buffers.CachedGapFreezeFrameBuffer.Should().BeNull();
    });

    [Fact]
    public Task CaptureFreeze_CopiesActualSnapshotDimensionsAndReusesExactPixels() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.Api.RenderRelease = release;
        Task<bool> capture = fixture.Session.TryCaptureGapFreezeFrameAsync(fixture.Session.CaptureGeneration(), () => true);
        await fixture.Api.RenderStarted.Task;
        fixture.Session.Width = 100;
        fixture.Session.Height = 100;
        release.Set();
        (await capture).Should().BeTrue();
        fixture.Buffers.CachedGapFreezeFrameWidth.Should().Be(2);
        fixture.Buffers.CachedGapFreezeFrameHeight.Should().Be(2);
        fixture.State = GapState.FreezeComplete;
        await fixture.Session.RenderGapAsync(GapRenderFrameDecision.GapFreeze);
        fixture.Spout.Frames.Should().ContainSingle().Which.Pixel.Should().Be(73);
    });

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
                fixture.NativeBuffers.PixelPtr.Should().NotBe(IntPtr.Zero);
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
        await WaitUntil(() => fixture.Scheduled.Count == 1);
        fixture.Scheduled.Should().ContainSingle();
        fixture.Session.Dispose();
        fixture.Scheduled[0]();
        callback(IntPtr.Zero);
        fixture.State = GapState.BlackFrameActive;
        await fixture.Session.RenderGapAsync(GapRenderFrameDecision.Black);
        fixture.Api.Calls.Select(c => c.Operation).Should().Equal("create", "callback", "update", "render", "free");
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
    public Task RenderedFinalFrame_IsCapturedWithoutPublishingAndReusedForFreeze() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.State = GapState.WaitingForFrameStep;
        (await fixture.Session.TryCaptureGapFreezeFrameAsync(fixture.Session.CaptureGeneration(), () => true)).Should().BeTrue();
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
        await WaitUntil(() => fixture.Scheduled.Count == 1);
        callback(IntPtr.Zero);
        await WaitUntil(() => fixture.Api.Calls.Count(c => c.Operation == "render") == 2);
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
        private readonly ConcurrentQueue<Action> _scheduled = new();
        public IReadOnlyList<Action> Scheduled => _scheduled.ToArray();
        public GapState State = GapState.Inactive;
        public bool FreezeConfirmed = true;
        public RenderSession Session { get; }
        public PixelBufferManager Buffers => (PixelBufferManager)typeof(RenderSession)
            .GetField("_buffers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Session)!;
        public PixelBufferManager NativeBuffers => (PixelBufferManager)typeof(RenderSession)
            .GetField("_nativeBuffers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Session)!;
        public Fixture(bool initialize = true)
        {
            Session = new RenderSession(Api, Spout, new PlaybackPerformanceStats(TimeSpan.FromSeconds(2)),
                () => State, () => GapBehavior.Freeze, _scheduled.Enqueue, () => FreezeConfirmed);
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
        public int RenderReturnCode;
        public volatile bool RepeatCallback;
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
            if (RepeatCallback)
            {
                Thread.Sleep(1);
                if (Callback!.TryGetTarget(out var callback)) callback(IntPtr.Zero);
            }
            return RenderReturnCode;
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

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
}
