using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TimecodeSyncPlayer.Tests;

public sealed class CombinedBitmapPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinedSend_CopiesBeforeSend_UnlocksBeforeObservers_AndKeepsDisjointTimings(bool traced)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        RenderFramePerformanceMeasurement? measurement = null;
        try
        {
            OnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(traced ? path : null);
                using var buffers = new PixelBufferManager();
                var calls = new List<string>();
                var renderer = new FrameRenderer(trace,
                    unlockCombinedBitmap: bitmap => { calls.Add("unlock"); bitmap.Unlock(); },
                    tryLockCombinedBitmap: bitmap => { bitmap.Lock(); return true; });
                renderer.BitmapChanged += _ => calls.Add("created");
                var pipeline = new RenderFramePublishPipeline(
                    _ => throw new InvalidOperationException("Ordinary path must not run"),
                    (pointer, _, _) =>
                    {
                        Assert.Equal(73, Marshal.ReadByte(pointer));
                        Assert.Equal(73, Marshal.ReadByte(renderer.CurrentBitmap!.BackBuffer));
                        calls.Add("send");
                        return 123;
                    },
                    value => { Assert.True(renderer.CurrentBitmap!.CanFreeze); calls.Add("stats"); measurement = value; },
                    (_, _, _, _) => { calls.Add("freeze"); return true; },
                    _ => calls.Add("preview"),
                    updateDisplayWithSpout: renderer.UpdateCombined);
                pipeline.PublishNormal(Enumerable.Repeat((byte)73, 16).ToArray(), 2, 2, 7, true,
                    GapState.Inactive, trace, 11, 4, 101, combineBitmapAndSpout: true);
                Assert.Equal(new[] { "created", "send", "unlock", "stats", "freeze", "preview" }, calls);
                Assert.Equal(123, measurement!.SpoutMs);
            });
            if (!traced) { Assert.False(File.Exists(path)); return; }
            var rows = SyncAccuracyTraceTests.Read(path);
            var stages = rows.Where(x => x.GetProperty("type").GetString() == "render-stage").ToArray();
            JsonElement Stage(string name) => Assert.Single(stages.Where(x => x.GetProperty("stage").GetString() == name));
            long Start(string name) => Stage(name).GetProperty("startTicks").GetInt64();
            long End(string name) => Stage(name).GetProperty("endTicks").GetInt64();
            Assert.DoesNotContain(stages, x => x.GetProperty("stage").GetString() == "bitmap");
            Assert.DoesNotContain(stages, x => x.GetProperty("stage").GetString() == "bitmap-lock");
            Assert.Equal("acquired", Stage("bitmap-try-lock").GetProperty("outcome").GetString());
            Assert.True(End("bitmap-try-lock") <= Start("bitmap-copy-dirty"));
            Assert.True(End("bitmap-copy-dirty") <= Start("spout"));
            Assert.True(End("spout") <= Start("bitmap-unlock"));
            Assert.True(End("bitmap-unlock") <= Assert.Single(rows.Where(x => x.GetProperty("type").GetString() == "frame")).GetProperty("ticks").GetInt64());
            Assert.True(Start("bitmap-send-scope") <= Start("bitmap-try-lock"));
            Assert.True(End("bitmap-send-scope") >= End("bitmap-unlock"));
            long bitmapTicks = new[] { "bitmap-try-lock", "bitmap-copy-dirty", "bitmap-unlock" }.Sum(name => End(name) - Start(name));
            Assert.Equal(bitmapTicks * 1000.0 / Stopwatch.Frequency, measurement!.BitmapMs, 8);
            Assert.All(stages, row => Assert.Equal(101, row.GetProperty("sequence").GetInt64()));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("input")]
    [InlineData("send")]
    [InlineData("send-and-unlock")]
    public void Failure_UnlocksOnlyAcquiredBitmap_PreservesErrors_AndCancelsPending(string failureAt) => OnSta(() =>
    {
        using var buffers = new PixelBufferManager();
        int unlocks = 0, sends = 0, cancellations = 0;
        var sendError = new InvalidOperationException("send failure");
        var unlockError = new InvalidOperationException("unlock failure");
        var renderer = new FrameRenderer(
            cancelPreview: () => cancellations++, unlockCombinedBitmap: bitmap =>
            {
                unlocks++;
                bitmap.Unlock();
                if (failureAt == "send-and-unlock") throw unlockError;
            });
        if (failureAt == "lock") renderer.BitmapChanged += bitmap => bitmap.Freeze();
        var error = Record.Exception(() => renderer.UpdateFromPixelsWithSpout(
            failureAt == "input" ? null! : new byte[16], 2, 2, () => { sends++; throw sendError; }));
        Assert.NotNull(error);
        Assert.Equal(failureAt is "lock" or "input" ? 0 : 1, unlocks);
        Assert.Equal(failureAt is "lock" or "input" ? 0 : 1, sends);
        Assert.Equal(1, cancellations);
        if (failureAt == "input") Assert.IsType<ArgumentNullException>(error);
        if (failureAt == "send") Assert.Same(sendError, error);
        if (failureAt == "send-and-unlock")
        {
            var aggregate = Assert.IsType<AggregateException>(error);
            Assert.Equal(new Exception[] { sendError, unlockError }, aggregate.InnerExceptions);
        }
        if (failureAt != "lock")
        {
            if (failureAt != "input") Assert.True(renderer.CurrentBitmap!.CanFreeze);
            renderer.UpdateFromPixels(new byte[16], 2, 2); // Guard and acquired lock were released.
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyBitmap_SendsOnceBeforeLock_AndSendFailureDoesNotAcquireOrUnlock(bool sendThrows)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        RenderFramePerformanceMeasurement? measurement = null;
        try
        {
            OnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(path);
                using var buffers = new PixelBufferManager();
                int sends = 0, unlocks = 0, cancellations = 0;
                var sendError = new InvalidOperationException("busy send failure");
                var renderer = new FrameRenderer(trace,
                    unlockCombinedBitmap: bitmap =>
                    {
                        unlocks++;
                        Assert.Equal(73, Marshal.ReadByte(bitmap.BackBuffer));
                        bitmap.Unlock();
                    }, tryLockCombinedBitmap: _ => false);
                renderer.UpdateFromPixels(Enumerable.Repeat((byte)11, 16).ToArray(), 2, 2);
                var pipeline = new RenderFramePublishPipeline(_ => throw new InvalidOperationException("Ordinary path"),
                    (pointer, _, _) =>
                    {
                        sends++;
                        Assert.True(renderer.CurrentBitmap!.CanFreeze);
                        Assert.Equal(11, Marshal.ReadByte(renderer.CurrentBitmap.BackBuffer));
                        Assert.Equal(73, Marshal.ReadByte(pointer));
                        if (sendThrows) throw sendError;
                        return 123;
                    }, value => measurement = value, (_, _, _, _) => false,
                    cancelPreview: () => cancellations++, updateDisplayWithSpout: renderer.UpdateCombined);
                var error = Record.Exception(() => pipeline.PublishNormal(Enumerable.Repeat((byte)73, 16).ToArray(),
                    2, 2, 0, true, GapState.Inactive, trace, 11, 4, 101, true));
                Assert.Equal(1, sends);
                Assert.Equal(sendThrows ? 0 : 1, unlocks);
                Assert.Equal(sendThrows ? 1 : 0, cancellations);
                if (sendThrows)
                {
                    Assert.Same(sendError, error);
                    Assert.Null(measurement);
                    Assert.Equal(11, Marshal.ReadByte(renderer.CurrentBitmap!.BackBuffer));
                    renderer.UpdateFromPixels(new byte[16], 2, 2);
                }
                else { Assert.Null(error); Assert.Equal(123, measurement!.SpoutMs); }
            });
            var stages = SyncAccuracyTraceTests.Read(path).Where(x => x.GetProperty("type").GetString() == "render-stage").ToArray();
            JsonElement Stage(string name) => Assert.Single(stages.Where(x => x.GetProperty("stage").GetString() == name));
            long Start(string name) => Stage(name).GetProperty("startTicks").GetInt64();
            long End(string name) => Stage(name).GetProperty("endTicks").GetInt64();
            Assert.Equal("busy", Stage("bitmap-try-lock").GetProperty("outcome").GetString());
            Assert.True(End("bitmap-try-lock") <= Start("spout"));
            Assert.Equal(sendThrows ? "exception" : "call-returned", Stage("spout").GetProperty("outcome").GetString());
            if (sendThrows)
                Assert.DoesNotContain(stages, x => x.GetProperty("stage").GetString() is "bitmap-lock" or "bitmap-copy-dirty" or "bitmap-unlock" or "freeze-copy");
            else
            {
                Assert.True(End("spout") <= Start("bitmap-lock"));
                Assert.True(End("bitmap-lock") <= Start("bitmap-copy-dirty"));
                Assert.True(End("bitmap-copy-dirty") <= Start("bitmap-unlock"));
                long ticks = new[] { "bitmap-try-lock", "bitmap-lock", "bitmap-copy-dirty", "bitmap-unlock" }.Sum(name => End(name) - Start(name));
                Assert.Equal(ticks * 1000.0 / Stopwatch.Frequency, measurement!.BitmapMs, 8);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reentry_IsRejectedBeforeBlackChangesCpuBuffer_AndPipelineSkipsLaterObservers() => OnSta(() =>
    {
        using var buffers = new PixelBufferManager();
        buffers.EnsurePixelBuffer(2, 2);
        Array.Fill(buffers.PixelBuffer!, (byte)73);
        int unlocks = 0, cancelled = 0;
        var renderer = new FrameRenderer(
            unlockCombinedBitmap: bitmap => { unlocks++; bitmap.Unlock(); });
        var pipeline = new RenderFramePublishPipeline(_ => 0,
            (_, _, _) => { using var black = new OutputFrameFactory(buffers).Black(2, 2); renderer.Update(black); return 0; },
            _ => Assert.Fail("Performance must not receive failed publication"),
            (_, _, _, _) => throw new InvalidOperationException("Freeze must not run"),
            _ => Assert.Fail("Preview must not run"), () => cancelled++, renderer.UpdateCombined);
        Assert.Throws<InvalidOperationException>(() => pipeline.PublishNormal(new byte[16], 2, 2, 0, true,
            GapState.Inactive, combineBitmapAndSpout: true));
        Assert.All(buffers.PixelBuffer!, value => Assert.Equal(73, value));
        Assert.Equal(1, unlocks);
        Assert.Equal(1, cancelled);
        renderer.UpdateFromPixels(new byte[16], 2, 2);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledSpout_CannotEnterCombinedDelegate_EvenIfCallerRequestsIt(bool traced)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var trace = SyncAccuracyTrace.Create(traced ? path : null))
            {
                var calls = new List<string>();
                var pipeline = new RenderFramePublishPipeline(
                    _ => { calls.Add("bitmap"); return 0; },
                    (_, _, _) => { calls.Add("send"); return 0; }, _ => { }, (_, _, _, _) => false,
                    updateDisplayWithSpout: (_, _) => throw new InvalidOperationException("Combined must not run"));
                pipeline.PublishNormal(new byte[16], 2, 2, 0, false, GapState.Inactive, trace, 1, 1, 1, true);
                Assert.Equal(new[] { "bitmap", "send" }, calls);
            }
            if (traced)
            {
                var send = Assert.Single(SyncAccuracyTraceTests.Read(path).Where(x =>
                    x.GetProperty("type").GetString() == "render-stage" && x.GetProperty("stage").GetString() == "spout"));
                Assert.Equal("disabled-call-returned", send.GetProperty("outcome").GetString());
            }
        }
        finally { File.Delete(path); }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class NullSpout : ISpoutOutput
    {
        public bool IsEnabled { get; set; }
        public bool IsAvailable => false;
        public bool TryInitialize() => false;
        public void SendFrame(IntPtr pixels, int width, int height) { }
        public void Dispose() { }
    }
}
