using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace TimecodeSyncPlayer.Tests;

public class PreviewOutputIntegrationTests
{
    [Fact]
    public void FrozenAndBuffered_DoNotConsumeNormalBitmapStageAttribution()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            RunOnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(path);
                using var buffers = new PixelBufferManager();
                buffers.EnsureFrozenFrameBuffer(2, 2);
                var renderer = new OutputFrameTestHarness(buffers, new FakeSpout(), trace);
                using var scope = new BitmapRenderTraceScope(trace, 1, 0, 1, 2, 2);
                renderer.PublishFrozen(2, 2);
                renderer.PublishBuffered(new byte[16], 2, 2, sendSpout: false);
                Assert.Same(scope, BitmapRenderTraceScope.Take(trace, 2, 2));
            });
            Assert.DoesNotContain(SyncAccuracyTraceTests.Read(path), r => r.GetProperty("type").GetString() == "render-stage");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Pipeline_QueuesPreviewAfterExternalAndFreeze_AndIsolatesPreviewFailure(bool tracing, bool failPreview)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var calls = new List<string>();
            using (var trace = SyncAccuracyTrace.Create(tracing ? path : null))
            {
                var pipeline = new RenderFramePublishPipeline(
                    _ => { calls.Add("bitmap"); return 1; },
                    (pixels, _, _) => { Assert.Equal(73, Marshal.ReadByte(pixels)); calls.Add("spout"); return 2; },
                    _ => calls.Add("perf"),
                    (_, _, _, _) => { calls.Add("freeze"); return true; },
                    _ => { calls.Add("preview"); if (failPreview) throw new InvalidOperationException("preview only"); });
                pipeline.PublishNormal([73, 0, 0, 0], 1, 1, 3, true, GapState.Inactive, trace, 1, 2, 3);
                Assert.Equal(new[] { "bitmap", "spout", "perf", "freeze", "preview" }, calls);
            }
            if (tracing)
            {
                var events = SyncAccuracyTraceTests.Read(path).Where(x => x.GetProperty("type").GetString() == "render-stage").ToArray();
                Assert.Equal(new[] { "bitmap", "spout", "freeze-copy", "preview-prepare" }, events.Select(x => x.GetProperty("stage").GetString()));
                Assert.Equal(failPreview ? "exception" : "call-returned", events[^1].GetProperty("outcome").GetString());
                for (int i = 1; i < events.Length; i++)
                    Assert.True(events[i - 1].GetProperty("endTicks").GetInt64() <= events[i].GetProperty("startTicks").GetInt64());
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FullResolutionAndPreview_HaveSeparateStorage_AndTickReadsLatestRetainedBitmap()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            var timer = new ManualPreviewTimer();
            long now = 0;
            using var presenter = new PreviewFramePresenter(SyncAccuracyTrace.Disabled, timer, () => now, 1_000_000);
            var spout = new FakeSpout();
            var renderer = new OutputFrameTestHarness(buffers, spout, SyncAccuracyTrace.Disabled, presenter.QueueFrame);
            WriteableBitmap? preview = null;
            presenter.BitmapChanged += bitmap => preview = bitmap;
            var source = new byte[1920 * 1080 * 4];
            Array.Fill(source, (byte)73);
            var pipeline = new RenderFramePublishPipeline(
                frame => { renderer.Renderer.Update(frame); return 0; },
                (pixels, w, h) => { spout.SendFrame(pixels, w, h); return 0; },
                _ => { }, (_, _, _, _) => false, renderer.QueuePreviewFromCurrentBitmap);
            pipeline.PublishNormal(source, 1920, 1080, 0, false, GapState.Inactive);
            Assert.Null(preview);
            Assert.True(timer.IsEnabled);
            Assert.Equal((1920, 1080, (byte)73), Assert.Single(spout.Frames));
            var external = renderer.CurrentBitmap!;
            Assert.Equal((1920, 1080), (external.PixelWidth, external.PixelHeight));
            // The pending preview retains the bitmap object, not the source array
            // or a copy of its older pixels. Read the latest contents at the tick.
            Array.Fill(source, (byte)99);
            renderer.UpdateFromPixels(source, 1920, 1080);
            now = 40_000;
            timer.Fire();
            Assert.NotNull(preview);
            Assert.NotSame(external, preview);
            Assert.Equal((960, 540), (preview.PixelWidth, preview.PixelHeight));
            Assert.Equal(99, FirstPixel(preview));
            Assert.Equal(99, FirstPixel(external));
            Assert.False(timer.IsEnabled);
        });
    }

    [Fact]
    public void BlackFrozenGapAndBuffered_QueueTheirActualPixelsAfterSpout_AndPreviewFailureIsHarmless()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            var spout = new FakeSpout();
            var observations = new List<(string Kind, byte Pixel, int Sends)>();
            var renderer = new OutputFrameTestHarness(buffers, spout, SyncAccuracyTrace.Disabled,
                (bitmap, kind) =>
                {
                    observations.Add((kind, Marshal.ReadByte(bitmap.BackBuffer), spout.Frames.Count));
                    throw new InvalidOperationException("preview only");
                });
            renderer.PublishBlack(2, 2);
            renderer.PublishFrozen(2, 2); // Missing frozen data falls back to Black, once.
            buffers.EnsureFrozenFrameBuffer(2, 2);
            Array.Fill(buffers.FrozenFrameBuffer!, (byte)31);
            renderer.PublishFrozen(2, 2);
            buffers.CopyFrozenToGapFreezeFrame(2, 2);
            renderer.PublishGapFreeze(9, 9);
            renderer.PublishBuffered([47, 0, 0, 0], 1, 1, sendSpout: false); // Preview still updates without a Spout pointer.
            Assert.Equal(new[] { ("black", (byte)0, 1), ("black", (byte)0, 2), ("frozen", (byte)31, 3),
                ("buffered", (byte)31, 4), ("buffered", (byte)47, 4) }, observations);
            Assert.Equal((1, 1), (renderer.CurrentBitmap!.PixelWidth, renderer.CurrentBitmap.PixelHeight));
        });
    }

    [Fact]
    public void PartialInput_PreviewReadsTheVisibleBitmapTail_NotAnEarlierSourceBuffer()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            byte[]? observed = null;
            var renderer = new OutputFrameTestHarness(buffers, new FakeSpout(), SyncAccuracyTrace.Disabled,
                (bitmap, _) => { observed = new byte[bitmap.BackBufferStride * bitmap.PixelHeight]; Marshal.Copy(bitmap.BackBuffer, observed, 0, observed.Length); });
            renderer.UpdateFromPixels([17, 17, 17, 17, 31, 31, 31, 31], 2, 1);
            renderer.UpdateFromPixels([47, 47, 47, 47], 2, 1);
            renderer.QueuePreviewFromCurrentBitmap("normal");
            Assert.Equal(new byte[] { 47, 47, 47, 47, 31, 31, 31, 31 }, observed);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPublication_CancelsRetainedMutableBitmapBeforeAnyPreviewTick(bool tracing)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            RunOnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(tracing ? path : null);
                using var buffers = new PixelBufferManager();
                var timer = new ManualPreviewTimer();
                long now = 0;
                using var presenter = new PreviewFramePresenter(trace, timer, () => now, 1_000_000);
                var renderer = new OutputFrameTestHarness(buffers, new FakeSpout(), trace, presenter.QueueFrame, presenter.ResetPending);
                int previews = 0;
                presenter.BitmapChanged += _ => previews++;
                renderer.UpdateFromPixels([17, 17, 17, 17, 31, 31, 31, 31], 2, 1);
                renderer.QueuePreviewFromCurrentBitmap("normal");
                Assert.True(timer.IsEnabled);
                var failure = new InvalidOperationException("failure after part of the bitmap changed");
                var pipeline = new RenderFramePublishPipeline(
                    _ => { renderer.UpdateFromPixels([47, 47, 47, 47], 2, 1); throw failure; },
                    (_, _, _) => throw new InvalidOperationException("Spout must not run"),
                    _ => throw new InvalidOperationException("perf must not run"),
                    (_, _, _, _) => false, renderer.QueuePreviewFromCurrentBitmap, presenter.ResetPending);
                Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
                    pipeline.PublishNormal([47, 47, 47, 47, 0, 0, 0, 0], 2, 1, 0, false, GapState.Inactive, trace, 1, 0, 1)));
                now = 40_000; timer.Fire();
                Assert.Equal(0, previews);
                Assert.False(timer.IsEnabled);
            });
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("black")]
    [InlineData("frozen")]
    [InlineData("buffered")]
    [InlineData("gap")]
    public void DirectOutputFailure_CancelsRetainedPreviewForEveryWritingPath(string operation)
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            var timer = new ManualPreviewTimer();
            long now = 0;
            using var presenter = new PreviewFramePresenter(SyncAccuracyTrace.Disabled, timer, () => now, 1_000_000);
            var spout = new FakeSpout();
            var renderer = new OutputFrameTestHarness(buffers, spout, SyncAccuracyTrace.Disabled, presenter.QueueFrame, presenter.ResetPending);
            int previews = 0;
            presenter.BitmapChanged += _ => previews++;
            renderer.UpdateFromPixels([17, 17, 17, 17], 1, 1);
            renderer.QueuePreviewFromCurrentBitmap("normal");
            buffers.EnsureFrozenFrameBuffer(1, 1);
            Array.Fill(buffers.FrozenFrameBuffer!, (byte)31);
            buffers.CopyFrozenToGapFreezeFrame(1, 1);
            spout.Failure = new InvalidOperationException("send failed");
            Action action = operation switch
            {
                "copy" => () => renderer.UpdateFromPixels(null!, 1, 1),
                "black" => () => renderer.PublishBlack(1, 1),
                "frozen" => () => renderer.PublishFrozen(1, 1),
                "gap" => () => renderer.PublishGapFreeze(1, 1),
                _ => () => renderer.PublishBuffered([47, 47, 47, 47], 1, 1)
            };
            if (operation == "copy") Assert.Throws<NullReferenceException>(action);
            else Assert.Same(spout.Failure, Assert.Throws<InvalidOperationException>(action));
            now = 40_000; timer.Fire();
            Assert.Equal(0, previews);
            Assert.False(timer.IsEnabled);
        });
    }

    internal sealed class ManualPreviewTimer : IPreviewFrameTimer
    {
        public event Action? Tick;
        public bool IsEnabled { get; private set; }
        public bool Disposed { get; private set; }
        public TimeSpan Interval { get; set; }
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void Fire() => Tick?.Invoke(); // Also exercise a callback already queued before cancellation.
        public void Dispose() { Disposed = true; IsEnabled = false; }
    }

    private sealed class FakeSpout : ISpoutOutput
    {
        public List<(int Width, int Height, byte Pixel)> Frames { get; } = [];
        public Exception? Failure;
        public bool IsEnabled { get; set; }
        public bool IsAvailable => true;
        public bool TryInitialize() => true;
        public void SendFrame(IntPtr pixels, int width, int height)
        {
            if (Failure != null) throw Failure;
            Frames.Add((width, height, Marshal.ReadByte(pixels)));
        }
        public void Dispose() { }
    }
    private static byte FirstPixel(WriteableBitmap bitmap)
    {
        var bytes = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), bytes, 4, 0);
        return bytes[0];
    }
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
