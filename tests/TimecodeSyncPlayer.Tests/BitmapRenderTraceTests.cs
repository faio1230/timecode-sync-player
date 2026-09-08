using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace TimecodeSyncPlayer.Tests;

public class BitmapRenderTraceTests
{
    [Fact]
    public void NestedPublication_KeepsItsIdentity_AndDirectReentryCannotBorrowParentIdentity()
    {
        string path = NewPath();
        try
        {
            RunOnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(path);
                using var outerBuffers = new PixelBufferManager();
                using var innerBuffers = new PixelBufferManager();
                using var directBuffers = new PixelBufferManager();
                var outer = new FrameRenderer(outerBuffers, new NullSpout(), trace);
                var inner = new FrameRenderer(innerBuffers, new NullSpout(), trace);
                var direct = new FrameRenderer(directBuffers, new NullSpout(), trace);
                var innerPipeline = Pipeline(inner);
                outer.BitmapChanged += _ =>
                {
                    Publish(innerPipeline, trace, 22, 5, 102);
                    // Same trace and dimensions, but no publication scope of its own.
                    direct.UpdateFromPixels(new byte[16], 2, 2);
                };
                Publish(Pipeline(outer), trace, 11, 4, 101);
            });
            JsonElement[] stages = Substages(path);
            Assert.Equal(6, stages.Length);
            foreach (var (session, generation, sequence) in new[] { (11L, 4, 101L), (22L, 5, 102L) })
            {
                var group = stages.Where(x => x.GetProperty("sessionId").GetInt64() == session).ToArray();
                Assert.Equal(new[] { "bitmap-lock", "bitmap-copy-dirty", "bitmap-unlock" },
                    group.Select(x => x.GetProperty("stage").GetString()));
                foreach (var row in group)
                {
                    Assert.Equal(generation, row.GetProperty("generation").GetInt32());
                    Assert.Equal(sequence, row.GetProperty("sequence").GetInt64());
                    Assert.Equal("completed", row.GetProperty("outcome").GetString());
                    Assert.Equal(2, row.GetProperty("width").GetInt32());
                    Assert.Equal(2, row.GetProperty("height").GetInt32());
                    Assert.True(row.GetProperty("startTicks").GetInt64() <= row.GetProperty("endTicks").GetInt64());
                }
                Assert.True(group[0].GetProperty("endTicks").GetInt64() <= group[1].GetProperty("startTicks").GetInt64());
                Assert.True(group[1].GetProperty("endTicks").GetInt64() <= group[2].GetProperty("startTicks").GetInt64());
            }
            var footer = SyncAccuracyTraceTests.Read(path)[^1];
            Assert.Equal(0, footer.GetProperty("errors").GetInt64());
            Assert.Equal(0, footer.GetProperty("dropped").GetInt64());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CopyFailure_UnlocksBitmap_PreservesException_AndRestoresScope()
    {
        string path = NewPath();
        try
        {
            RunOnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(path);
                using var buffers = new PixelBufferManager();
                var renderer = new FrameRenderer(buffers, new NullSpout(), trace);
                WriteableBitmap? bitmap = null;
                renderer.BitmapChanged += value => bitmap = value;
                var pipeline = Pipeline(renderer);
                Assert.Throws<NullReferenceException>(() => pipeline.Publish(null!, 2, 2, 0, false,
                    GapState.Inactive, trace, 11, 4, 101));
                Assert.NotNull(bitmap);
                Assert.True(bitmap.CanFreeze, "A bitmap still locked after the failed copy cannot freeze.");
                // Neither this direct call nor its marker event should get failed publication metadata.
                renderer.UpdateFromPixels(new byte[16], 2, 2);
                Publish(pipeline, trace, 11, 4, 102);
            });
            var stages = Substages(path);
            Assert.Equal(6, stages.Length);
            var failed = stages.Where(x => x.GetProperty("sequence").GetInt64() == 101).ToArray();
            Assert.Equal(new[] { "completed", "exception", "completed" },
                failed.Select(x => x.GetProperty("outcome").GetString()));
            Assert.All(stages.Where(x => x.GetProperty("sequence").GetInt64() == 102),
                row => Assert.Equal("completed", row.GetProperty("outcome").GetString()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LockFailure_DoesNotAttemptCopyOrUnlock()
    {
        string path = NewPath();
        try
        {
            RunOnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(path);
                using var buffers = new PixelBufferManager();
                var renderer = new FrameRenderer(buffers, new NullSpout(), trace);
                renderer.BitmapChanged += bitmap => bitmap.Freeze();
                Assert.Throws<InvalidOperationException>(() => Publish(Pipeline(renderer), trace, 11, 4, 101));
            });
            var stage = Assert.Single(Substages(path));
            Assert.Equal("bitmap-lock", stage.GetProperty("stage").GetString());
            Assert.Equal("exception", stage.GetProperty("outcome").GetString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Scope_DoesNotFlowToAnotherThreadOrTrace_AndIsConsumedOnce()
    {
        string path = NewPath(), otherPath = NewPath();
        try
        {
            using var trace = SyncAccuracyTrace.Create(path);
            using var otherTrace = SyncAccuracyTrace.Create(otherPath);
            using (var scope = new BitmapRenderTraceScope(trace, 1, 2, 3, 2, 2))
            {
                Assert.Null(BitmapRenderTraceScope.Take(otherTrace, 2, 2));
                Assert.Null(BitmapRenderTraceScope.Take(trace, 4, 4));
                RunOnSta(() => Assert.Null(BitmapRenderTraceScope.Take(trace, 2, 2)));
                Assert.Same(scope, BitmapRenderTraceScope.Take(trace, 2, 2));
                Assert.Null(BitmapRenderTraceScope.Take(trace, 2, 2));
            }
            Assert.Null(BitmapRenderTraceScope.Take(trace, 2, 2));
        }
        finally { File.Delete(path); File.Delete(otherPath); }
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
    private static JsonElement[] Substages(string path) => SyncAccuracyTraceTests.Read(path)
        .Where(x => x.GetProperty("type").GetString() == "render-stage" &&
            x.GetProperty("stage").GetString() is "bitmap-lock" or "bitmap-copy-dirty" or "bitmap-unlock")
        .ToArray();
    private static RenderFramePublishPipeline Pipeline(FrameRenderer renderer) => new(
        (pixels, width, height) => { renderer.UpdateFromPixels(pixels, width, height); return 0; },
        (_, _, _) => 0, _ => { }, (_, _, _, _) => false);
    private static void Publish(RenderFramePublishPipeline pipeline, SyncAccuracyTrace trace,
        long session, int generation, long sequence) => pipeline.Publish(new byte[16], 2, 2, 0,
            false, GapState.Inactive, trace, session, generation, sequence);

    private static void RunOnSta(Action action)
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
