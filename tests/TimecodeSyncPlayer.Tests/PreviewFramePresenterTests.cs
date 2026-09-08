using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TimecodeSyncPlayer.Tests;

public class PreviewFramePresenterTests
{
    [Fact]
    public void BitmapQueueReadsLatestPixelsAtTick_AndNeverDisplaysTheFullSourceObject()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            var source = NewBitmap(2, 2, 17);
            fixture.Presenter.QueueFrame(source, "normal");
            FillBitmap(source, 31); // Same object changed after Queue returned.
            Assert.Empty(fixture.Bitmaps);
            fixture.Now = 100; fixture.Timer.Fire();
            var preview = Assert.Single(fixture.Bitmaps);
            Assert.NotSame(source, preview);
            AssertPixels(preview, 31);
            FillBitmap(source, 47);
            fixture.Now = 200; fixture.Timer.Fire();
            AssertPixels(preview, 31); // Consumed reference is no longer pending.
        });
    }

    [Fact]
    public void BitmapQueueLatestReplacementAndPointerQueueSupersedeEachOther()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            var first = NewBitmap(2, 2, 17);
            var latest = NewBitmap(3, 1, 31);
            fixture.Presenter.QueueFrame(first, "black");
            fixture.Presenter.QueueFrame(latest, "frozen");
            fixture.Now = 100; fixture.Timer.Fire();
            Assert.Equal((3, 1), (fixture.Bitmaps[0].PixelWidth, fixture.Bitmaps[0].PixelHeight));
            AssertPixels(fixture.Bitmaps[0], 31);
            using var pointer = new NativePixels(2, 2);
            pointer.Fill(47);
            fixture.Presenter.QueueFrame(latest, "normal");
            fixture.Queue(pointer);
            fixture.Now = 200; fixture.Timer.Fire();
            AssertPixels(fixture.Bitmaps[^1], 47);
            fixture.Queue(pointer);
            fixture.Presenter.QueueFrame(first, "normal");
            fixture.Now = 300; fixture.Timer.Fire();
            AssertPixels(fixture.Bitmaps[^1], 17);
        });
    }

    [Fact]
    public void BusyPreviewRetainsBitmapSource_AndNextTickReadsItsNewestContent()
    {
        RunOnSta(() =>
        {
            int attempts = 0;
            using var fixture = new Fixture(bitmap => ++attempts > 1 && bitmap.TryLock(new Duration(TimeSpan.Zero)));
            var source = NewBitmap(2, 2, 17);
            fixture.Presenter.QueueFrame(source, "normal");
            fixture.Now = 100; fixture.Timer.Fire();
            Assert.Empty(fixture.Bitmaps);
            FillBitmap(source, 31); // No new Queue: the pending object stays live.
            fixture.Now = 200; fixture.Timer.Fire();
            AssertPixels(Assert.Single(fixture.Bitmaps), 31);
            Assert.False(fixture.Timer.IsEnabled);
        });
    }

    [Fact]
    public void FrameRateSwitchPreservesPendingAndFlushesTheLastFrameAtTheNewLimit()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            var source = NewBitmap(2, 2, 17);
            fixture.Presenter.QueueFrame(source, "normal");
            fixture.Now = 50;
            fixture.Presenter.SetMaximumFramesPerSecond(10);
            Assert.Equal(250.0 / 3000, fixture.Timer.Interval.TotalSeconds, 6);
            fixture.Now = 100; fixture.Timer.Fire();
            Assert.Empty(fixture.Bitmaps);
            fixture.Now = 300; fixture.Timer.Fire();
            var preview = Assert.Single(fixture.Bitmaps);
            AssertPixels(preview, 17);
            FillBitmap(source, 31);
            fixture.Now = 350; fixture.Presenter.QueueFrame(source, "normal");
            int intervalWrites = fixture.Timer.IntervalWrites;
            fixture.Presenter.SetMaximumFramesPerSecond(10);
            Assert.Equal(intervalWrites, fixture.Timer.IntervalWrites);
            fixture.Now = 400;
            fixture.Presenter.SetMaximumFramesPerSecond(30);
            fixture.Timer.Fire();
            AssertPixels(preview, 31);
            FillBitmap(source, 47);
            fixture.Now = 450;
            fixture.Presenter.QueueFrame(source, "frozen");
            fixture.Presenter.SetMaximumFramesPerSecond(10);
            fixture.Now = 699; fixture.Timer.Fire();
            AssertPixels(preview, 31);
            fixture.Now = 700; fixture.Timer.Fire();
            AssertPixels(preview, 47);
            Assert.False(fixture.Timer.IsEnabled);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    public void InvalidFrameRateDoesNotThrowOrChangeAValidPendingDeadline(int fps)
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Presenter.QueueFrame(NewBitmap(2, 2, 17), "normal");
            var interval = fixture.Timer.Interval;
            fixture.Presenter.SetMaximumFramesPerSecond(fps);
            Assert.Equal(interval, fixture.Timer.Interval);
            fixture.Now = 100; fixture.Timer.Fire();
            AssertPixels(Assert.Single(fixture.Bitmaps), 17);
        });
    }

    [Fact]
    public void BitmapPendingIsCancelledByOffUiResetAndDispose_AndNewEpochSurvivesOldCleanup()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            var oldSource = NewBitmap(2, 2, 17);
            var newSource = NewBitmap(3, 1, 31);
            fixture.Presenter.QueueFrame(oldSource, "normal");
            RunOnWorker(fixture.Presenter.ResetPending);
            fixture.Presenter.QueueFrame(newSource, "frozen");
            DrainDispatcher();
            fixture.Now = 100; fixture.Timer.Fire();
            AssertPixels(Assert.Single(fixture.Bitmaps), 31);
            fixture.Presenter.QueueFrame(oldSource, "normal");
            fixture.Presenter.ResetPending();
            fixture.Now = 200; fixture.Timer.Fire();
            Assert.Single(fixture.Bitmaps);
            fixture.Presenter.QueueFrame(oldSource, "normal");
            RunOnWorker(fixture.Presenter.Dispose);
            fixture.Now = 300; fixture.Timer.CaptureTick()?.Invoke();
            fixture.Presenter.QueueFrame(newSource, "normal");
            DrainDispatcher();
            Assert.Single(fixture.Bitmaps);
            Assert.Equal(1, fixture.Timer.Disposals);
        });
    }

    [Fact]
    public void BitmapSourceMustBelongToTheUiThreadAndUseBgr32()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            WriteableBitmap? otherThreadSource = null;
            RunOnSta(() => otherThreadSource = NewBitmap(2, 2, 17));
            fixture.Presenter.QueueFrame(otherThreadSource!, "normal");
            fixture.Presenter.QueueFrame(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null), "normal");
            Assert.False(fixture.Timer.IsEnabled);
            Assert.Empty(fixture.Bitmaps);
            fixture.Presenter.QueueFrame(NewBitmap(2, 2, 31), "normal");
            fixture.Now = 100; fixture.Timer.Fire();
            AssertPixels(Assert.Single(fixture.Bitmaps), 31);
        });
    }

    [Theory]
    [InlineData(3840, 2160, 960, 540)]
    [InlineData(4096, 2160, 960, 506)]
    [InlineData(2160, 3840, 303, 540)]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(400, 300, 400, 300)]
    [InlineData(1, 2000, 1, 540)]
    public void SizePolicy_PreservesAspectWithinRoundingAndNeverUpscales(int w, int h, int expectedW, int expectedH)
    {
        var size = PreviewFrameScaler.ValidateAndGetSize(new IntPtr(1), w, h, w * 4);
        Assert.Equal((expectedW, expectedH), size);
        Assert.InRange(size.Width * size.Height * 4, 4, PreviewFrameScaler.MaximumBytes);
    }

    [Theory]
    [InlineData(0, 2, 8)]
    [InlineData(2, -1, 8)]
    [InlineData(2, 2, 7)]
    [InlineData(2, 2, -8)]
    [InlineData(2, int.MaxValue, 8)]
    [InlineData(int.MaxValue, 1, int.MaxValue)]
    public void SourceLayout_InvalidDimensionsOrShortStrideAreRejected(int w, int h, int stride)
        => Assert.Throws<ArgumentException>(() => PreviewFrameScaler.ValidateAndGetSize(new IntPtr(1), w, h, stride));

    [Fact]
    public void QueueCopiesSynchronously_OwnsOnlyLatestFrame_AndFlushesAfterInputStops()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using (var source = new NativePixels(2, 2, 12))
            {
                source.Fill(17);
                fixture.Presenter.QueueFrame(source.Pointer, 2, 2, 12, "normal");
                source.Fill(31);
                fixture.Presenter.QueueFrame(source.Pointer, 2, 2, 12, "frozen");
                source.Fill(99); // Source can change and be released before the timer.
            }
            Assert.Equal(1, fixture.Timer.Starts);
            Assert.Empty(fixture.Bitmaps);
            fixture.Now = 100; fixture.Timer.Fire();
            AssertPixels(Assert.Single(fixture.Bitmaps), 31);
            Assert.False(fixture.Timer.IsEnabled);
            fixture.Now = 200; fixture.Timer.Fire();
            Assert.Single(fixture.Bitmaps);
        });
    }

    [Fact]
    public void ScalerUsesNearestNeighborAndIgnoresSourceRowPadding()
    {
        using var source = new NativePixels(1920, 2, 1920 * 4 + 16);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 1920; x++)
                Marshal.WriteInt32(source.Pointer, y * source.Stride + x * 4, y * 10000 + x);
        var size = PreviewFrameScaler.ValidateAndGetSize(source.Pointer, 1920, 2, source.Stride);
        Assert.Equal((960, 1), size);
        var pixels = new byte[size.Width * size.Height * 4];
        PreviewFrameScaler.Copy(source.Pointer, 1920, 2, source.Stride, pixels, size.Width, size.Height);
        for (int x = 0; x < 960; x++) Assert.Equal(x * 2, BitConverter.ToInt32(pixels, x * 4));
    }

    [Fact]
    public void ContinuousInputKeepsDeadlineAndDoesNotRestartAFullPeriodAfterEveryFrame()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var source = new NativePixels(2, 2);
            source.Fill(17);
            fixture.Queue(source);
            int intervalWrites = fixture.Timer.IntervalWrites;
            fixture.Now = 50; fixture.Queue(source);
            Assert.Equal(1, fixture.Timer.Starts);
            Assert.Equal(intervalWrites, fixture.Timer.IntervalWrites);
            fixture.Now = 99; fixture.Timer.Fire();
            Assert.Empty(fixture.Bitmaps);
            fixture.Now = 100; fixture.Timer.Fire();
            var bitmap = Assert.Single(fixture.Bitmaps);
            source.Fill(31);
            fixture.Now = 150; fixture.Queue(source);
            Assert.Equal(50.0 / 3000, fixture.Timer.Interval.TotalSeconds, 6);
            fixture.Now = 199; fixture.Timer.Fire();
            AssertPixels(bitmap, 17);
            fixture.Now = 200; fixture.Timer.Fire();
            AssertPixels(bitmap, 31);
            Assert.Single(fixture.Bitmaps); // Same bitmap: no per-frame source event.
        });
    }

    [Fact]
    public void BusyBitmapDoesNotWait_AndNewestReplacementIsRetriedNextTick()
    {
        RunOnSta(() =>
        {
            int attempts = 0;
            using var fixture = new Fixture(bitmap => ++attempts > 1 && bitmap.TryLock(new System.Windows.Duration(TimeSpan.Zero)));
            using var source = new NativePixels(2, 2);
            source.Fill(17); fixture.Queue(source);
            fixture.Now = 100; fixture.Timer.Fire();
            Assert.Empty(fixture.Bitmaps);
            Assert.True(fixture.Timer.IsEnabled);
            source.Fill(31); fixture.Now = 150; fixture.Queue(source);
            fixture.Now = 200; fixture.Timer.Fire();
            Assert.Equal(2, attempts);
            AssertPixels(Assert.Single(fixture.Bitmaps), 31);
            Assert.False(fixture.Timer.IsEnabled);
        });
    }

    [Fact]
    public void ResizeAndResetReplacePending_KeepDisplayedImage_AndDisposeMakesCallbacksHarmless()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var small = new NativePixels(2, 2);
            using var wide = new NativePixels(3, 1);
            small.Fill(17); fixture.Queue(small);
            fixture.Now = 100; fixture.Timer.Fire();
            var displayed = Assert.Single(fixture.Bitmaps);
            wide.Fill(31); fixture.Queue(wide);
            fixture.Presenter.ResetPending();
            Assert.False(fixture.Timer.IsEnabled);
            fixture.Now = 200; fixture.Timer.Fire();
            Assert.Single(fixture.Bitmaps);
            AssertPixels(displayed, 17);
            fixture.Queue(small); fixture.Queue(wide);
            fixture.Now = 300; fixture.Timer.Fire();
            Assert.Equal(2, fixture.Bitmaps.Count);
            Assert.Equal((3, 1), (fixture.Bitmaps[^1].PixelWidth, fixture.Bitmaps[^1].PixelHeight));
            AssertPixels(fixture.Bitmaps[^1], 31);
            var lateTick = fixture.Timer.CaptureTick();
            fixture.Queue(small);
            fixture.Presenter.Dispose();
            fixture.Presenter.Dispose();
            fixture.Presenter.QueueFrame(IntPtr.Zero, -1, -1, -1, "invalid-after-dispose");
            fixture.Now = 400; lateTick?.Invoke();
            Assert.Equal(2, fixture.Bitmaps.Count);
            Assert.Equal(1, fixture.Timer.Disposals);
            Assert.False(fixture.Timer.IsEnabled);
        });
    }

    [Fact]
    public void OffUiCancellationHasNoDispatcherWait_AndStaleCleanupDoesNotEraseNewQueue()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var source = new NativePixels(2, 2);
            source.Fill(17); fixture.Queue(source);
            RunOnWorker(fixture.Presenter.ResetPending);
            fixture.Now = 100; fixture.Timer.Fire();
            Assert.Empty(fixture.Bitmaps);
            source.Fill(31); fixture.Queue(source);
            DrainDispatcher(); // Async cleanup belongs to the older queue epoch.
            fixture.Now = 200; fixture.Timer.Fire();
            AssertPixels(Assert.Single(fixture.Bitmaps), 31);
            fixture.Queue(source);
            var staleTick = fixture.Timer.CaptureTick();
            RunOnWorker(fixture.Presenter.Dispose);
            fixture.Now = 300; staleTick?.Invoke();
            Assert.Single(fixture.Bitmaps);
            DrainDispatcher();
            Assert.Equal(1, fixture.Timer.Disposals);
            Assert.False(fixture.Timer.IsEnabled);
        });
    }

    [Fact]
    public void InvalidQueueAndPreviewFaultsDoNotEscape_AndLaterInputRecovers()
    {
        RunOnSta(() =>
        {
            int attempts = 0;
            using var fixture = new Fixture(bitmap => ++attempts == 1
                ? throw new InvalidOperationException("test lock fault")
                : bitmap.TryLock(new System.Windows.Duration(TimeSpan.Zero)));
            using var source = new NativePixels(2, 2);
            source.Fill(17); fixture.Queue(source);
            fixture.Presenter.QueueFrame(IntPtr.Zero, 2, 2, 8, "invalid");
            fixture.Now = 100; fixture.Timer.Fire();
            Assert.Empty(fixture.Bitmaps);
            Assert.False(fixture.Timer.IsEnabled);
            fixture.Queue(source);
            fixture.Now = 200; fixture.Timer.Fire();
            AssertPixels(Assert.Single(fixture.Bitmaps), 17);
            fixture.Presenter.BitmapChanged += _ => throw new InvalidOperationException("test observer fault");
            using var resized = new NativePixels(3, 2);
            fixture.Queue(resized);
            fixture.Now = 300; fixture.Timer.Fire(); // No exception crosses the timer boundary.
        });
    }

    [Fact]
    public void BlackAndFreezeAreLatestFrames_AndUseSeparatePreviewTraceEvents()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            RunOnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(path);
                using var fixture = new Fixture(trace: trace);
                using var source = new NativePixels(2, 2);
                source.Fill(0); fixture.Queue(source, "black");
                fixture.Now = 100; fixture.Timer.Fire();
                AssertPixels(Assert.Single(fixture.Bitmaps), 0);
                source.Fill(31); fixture.Queue(source, "frozen");
                fixture.Now = 200; fixture.Timer.Fire();
                AssertPixels(fixture.Bitmaps[0], 31);
            });
            var rows = SyncAccuracyTraceTests.Read(path);
            var frames = rows.Where(row => row.GetProperty("type").GetString() == "preview-frame").ToArray();
            Assert.Equal(new[] { "black", "frozen" }, frames.Select(row => row.GetProperty("kind").GetString()));
            Assert.DoesNotContain(rows, row => row.GetProperty("type").GetString() == "frame");
            Assert.Equal(0, rows[^1].GetProperty("errors").GetInt64());
            Assert.Equal(0, rows[^1].GetProperty("dropped").GetInt64());
        }
        finally { File.Delete(path); }
    }

    private static void AssertPixels(WriteableBitmap bitmap, byte expected)
    {
        var readback = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(readback, bitmap.PixelWidth * 4, 0);
        Assert.Equal(-1, readback.AsSpan().IndexOfAnyExcept(expected));
    }

    private static WriteableBitmap NewBitmap(int width, int height, byte value)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        FillBitmap(bitmap, value);
        return bitmap;
    }

    private static void FillBitmap(WriteableBitmap bitmap, byte value)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        Array.Fill(pixels, value);
        bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), pixels, bitmap.PixelWidth * 4, 0);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action) => RunThread(action, sta: true);
    private static void RunOnWorker(Action action) => RunThread(action, sta: false);
    private static void RunThread(Action action, bool sta)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        if (sta) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Preview cancellation must not synchronously wait for its blocked UI thread.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture : IDisposable
    {
        public readonly FakeTimer Timer = new();
        public readonly PreviewFramePresenter Presenter;
        public readonly List<WriteableBitmap> Bitmaps = [];
        public long Now;
        public Fixture(Func<WriteableBitmap, bool>? tryLock = null, SyncAccuracyTrace? trace = null)
        {
            Presenter = new PreviewFramePresenter(trace ?? SyncAccuracyTrace.Disabled, Timer, () => Now, 3000, tryLock);
            Presenter.BitmapChanged += Bitmaps.Add;
        }
        public void Queue(NativePixels source, string kind = "normal")
            => Presenter.QueueFrame(source.Pointer, source.Width, source.Height, source.Stride, kind);
        public void Dispose() => Presenter.Dispose();
    }

    private sealed class FakeTimer : IPreviewFrameTimer
    {
        private TimeSpan _interval;
        public event Action? Tick;
        public bool IsEnabled { get; private set; }
        public int Starts, Stops, Disposals, IntervalWrites;
        public TimeSpan Interval { get => _interval; set { _interval = value; IntervalWrites++; } }
        public void Start() { IsEnabled = true; Starts++; }
        public void Stop() { IsEnabled = false; Stops++; }
        public void Dispose() { Stop(); Disposals++; Tick = null; }
        public void Fire() { if (IsEnabled) Tick?.Invoke(); }
        public Action? CaptureTick() => Tick;
    }

    private sealed class NativePixels : IDisposable
    {
        public IntPtr Pointer { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public NativePixels(int width, int height, int? stride = null)
        {
            Width = width; Height = height; Stride = stride ?? width * 4;
            Pointer = Marshal.AllocHGlobal(Stride * height);
            Fill(0);
        }
        public void Fill(byte value)
        {
            var data = new byte[Stride * Height];
            Array.Fill(data, value);
            Marshal.Copy(data, 0, Pointer, data.Length);
        }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
