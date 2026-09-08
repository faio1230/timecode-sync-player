using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;

namespace TimecodeSyncPlayer;

internal interface IPreviewFrameTimer : IDisposable
{
    event Action? Tick;
    bool IsEnabled { get; }
    TimeSpan Interval { get; set; }
    void Start();
    void Stop();
}

/// <summary>
/// UI-thread-only latest preview. The product queues one full bitmap reference;
/// only a due timer tick reads/scales its current pixels. No native pixel pointer
/// survives a call. The timer flushes the last frame after playback stops too.
/// </summary>
internal sealed class PreviewFramePresenter : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SyncAccuracyTrace _trace;
    private readonly IPreviewFrameTimer _timer;
    private readonly Func<long> _timestamp;
    private readonly long _frequency;
    private long _period;
    private int _maximumFramesPerSecond = 30;
    private readonly Func<WriteableBitmap, bool> _tryLock;
    private byte[]? _pixels;
    private WriteableBitmap? _sourceBitmap;
    private int _width, _height;
    private string _kind = "normal";
    private WriteableBitmap? _bitmap;
    private bool _bitmapNeedsNotification;
    private bool _pending, _inTick, _timerDisposed;
    private volatile bool _disposed;
    private long _epoch, _pendingEpoch;
    private long? _nextAllowed;
    private long? _lastPublished;

    public event Action<WriteableBitmap>? BitmapChanged;

    public PreviewFramePresenter(SyncAccuracyTrace? trace = null)
        : this(trace ?? SyncAccuracyTrace.Current, new PreviewDispatcherTimer(),
            Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

    internal PreviewFramePresenter(SyncAccuracyTrace trace, IPreviewFrameTimer timer,
        Func<long> timestamp, long timestampFrequency, Func<WriteableBitmap, bool>? tryLock = null)
    {
        if (timestampFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        _trace = trace;
        _timer = timer;
        _timestamp = timestamp;
        _frequency = timestampFrequency;
        _period = timestampFrequency / 30 + (timestampFrequency % 30 == 0 ? 0 : 1);
        _tryLock = tryLock ?? (bitmap => bitmap.TryLock(new Duration(TimeSpan.Zero)));
        _timer.Tick += OnTick;
    }

    public void QueueFrame(IntPtr pixels, int sourceWidth, int sourceHeight, int sourceStride, string kind)
    {
        if (_disposed || !CheckAccess()) return;
        try
        {
            long epoch = Volatile.Read(ref _epoch);
            var size = PreviewFrameScaler.ValidateAndGetSize(pixels, sourceWidth, sourceHeight, sourceStride);
            int bytes = size.Width * size.Height * 4;
            // Validation leaves a prior valid pending frame alone. Once writing
            // starts, a failed copy must not publish a partly replaced image.
            _pending = false;
            _sourceBitmap = null;
            if (_pixels == null || _pixels.Length < bytes) _pixels = new byte[bytes];
            PreviewFrameScaler.Copy(pixels, sourceWidth, sourceHeight, sourceStride, _pixels, size.Width, size.Height);
            if (_disposed || epoch != Volatile.Read(ref _epoch)) { ClearPending(); return; }
            _width = size.Width; _height = size.Height; _kind = kind;
            _pendingEpoch = epoch;
            _pending = true;
            if (!_timer.IsEnabled) Schedule();
        }
        catch (Exception ex)
        {
            Warn(ex, "queue");
            if (!_pending) StopTimer();
        }
    }

    /// <summary>
    /// Product path: retain only the latest UI-owned Bgr32 bitmap, not a snapshot
    /// lease or BackBuffer pointer. A tick reads its newest content, including
    /// changes made to this same bitmap after QueueFrame returned.
    /// </summary>
    public void QueueFrame(WriteableBitmap source, string kind)
    {
        if (_disposed || !CheckAccess()) return;
        try
        {
            long epoch = Volatile.Read(ref _epoch);
            if (source == null || !ReferenceEquals(source.Dispatcher, _dispatcher) || source.Format != PixelFormats.Bgr32)
                throw new ArgumentException("Preview source must be a Bgr32 bitmap owned by this UI thread.", nameof(source));
            _sourceBitmap = source;
            _kind = kind;
            _pendingEpoch = epoch;
            _pending = true;
            if (_disposed || epoch != Volatile.Read(ref _epoch)) { ClearPending(); return; }
            if (!_timer.IsEnabled) Schedule();
        }
        catch (Exception ex) { Warn(ex, "queue-bitmap"); }
    }

    public void SetMaximumFramesPerSecond(int fps)
    {
        if (_disposed || !CheckAccess()) return;
        try
        {
            if (fps < 1 || fps > 30) throw new ArgumentOutOfRangeException(nameof(fps));
            if (fps == _maximumFramesPerSecond) return;
            long oldPeriod = _period;
            _maximumFramesPerSecond = fps;
            _period = _frequency / fps + (_frequency % fps == 0 ? 0 : 1);
            if (_nextAllowed.HasValue)
                _nextAllowed = (_lastPublished ?? (_nextAllowed.Value - oldPeriod)) + _period;
            if (_pending) Schedule();
        }
        catch (Exception ex) { Warn(ex, "frame-rate"); }
    }

    private void Schedule()
    {
        long now = _timestamp();
        _nextAllowed ??= now + _period;
        long remaining = Math.Max(1, _nextAllowed.Value - now);
        _timer.Interval = TimeSpan.FromSeconds(remaining / (double)_frequency);
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void OnTick()
    {
        if (_disposed || _inTick || !CheckAccess()) return;
        _inTick = true;
        bool consumed = false;
        try
        {
            if (!_pending) return;
            if (_pendingEpoch != Volatile.Read(ref _epoch)) { ClearPending(); return; }
            long now = _timestamp();
            if (_nextAllowed.HasValue && now < _nextAllowed.Value) return;
            // This strong local keeps the current source alive only for this
            // synchronous UI tick; WPF/renderer cannot update it concurrently.
            WriteableBitmap? source = _sourceBitmap;
            if (source != null)
            {
                var size = PreviewFrameScaler.ValidateAndGetSize(source.BackBuffer,
                    source.PixelWidth, source.PixelHeight, source.BackBufferStride);
                int bytes = size.Width * size.Height * 4;
                if (_pixels == null || _pixels.Length < bytes) _pixels = new byte[bytes];
                PreviewFrameScaler.Copy(source.BackBuffer, source.PixelWidth, source.PixelHeight,
                    source.BackBufferStride, _pixels, size.Width, size.Height);
                _width = size.Width; _height = size.Height;
                if (_disposed || _pendingEpoch != Volatile.Read(ref _epoch)) { ClearPending(); return; }
            }
            if (_pixels == null) return;
            if (_bitmap == null || _bitmap.PixelWidth != _width || _bitmap.PixelHeight != _height)
            {
                _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgr32, null);
                _bitmapNeedsNotification = true;
            }
            WriteableBitmap bitmap = _bitmap;
            // No waiting on WPF: keep the newest pending pixels for the next tick.
            if (!_tryLock(bitmap)) { _nextAllowed = now + _period; return; }
            try
            {
                Marshal.Copy(_pixels, 0, bitmap.BackBuffer, _width * _height * 4);
                bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
            finally { bitmap.Unlock(); }
            string kind = _kind;
            _pending = false;
            _sourceBitmap = null;
            consumed = true;
            if (_disposed || _pendingEpoch != Volatile.Read(ref _epoch)) return;
            long published = _timestamp();
            _lastPublished = published;
            _nextAllowed = published + _period;
            _trace.RecordPreviewFrame(kind, bitmap.BackBuffer, bitmap.PixelWidth,
                bitmap.PixelHeight, bitmap.BackBufferStride, published);
            if (_disposed || _pendingEpoch != Volatile.Read(ref _epoch)) return;
            if (_bitmapNeedsNotification)
            {
                BitmapChanged?.Invoke(bitmap);
                _bitmapNeedsNotification = false;
            }
        }
        catch (Exception ex)
        {
            if (!consumed) { _pending = false; _sourceBitmap = null; }
            Warn(ex, "tick");
        }
        finally
        {
            _inTick = false;
            if (!_disposed)
            {
                try { if (_pending) Schedule(); else StopTimer(); }
                catch (Exception ex) { Warn(ex, "timer"); StopTimer(); }
            }
        }
    }

    public void ResetPending()
    {
        if (_disposed) return;
        long epoch = Interlocked.Increment(ref _epoch);
        if (_dispatcher.CheckAccess()) ResetOnUiThread(epoch);
        else PostToUi(() => ResetOnUiThread(epoch));
    }

    private void ResetOnUiThread(long epoch)
    {
        // A newer QueueFrame may have arrived before this asynchronous reset.
        if (_disposed || (_pending && _pendingEpoch >= epoch)) return;
        ClearPending();
    }

    private void ClearPending()
    {
        _pending = false;
        _pixels = null;
        _sourceBitmap = null;
        _nextAllowed = null;
        StopTimer();
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _epoch);
        if (_dispatcher.CheckAccess()) DisposeOnUiThread();
        else PostToUi(DisposeOnUiThread);
    }

    private void DisposeOnUiThread()
    {
        if (_timerDisposed) return;
        _timerDisposed = true;
        _pending = false;
        _pixels = null;
        _sourceBitmap = null;
        _bitmap = null;
        BitmapChanged = null;
        try { _timer.Tick -= OnTick; }
        catch (Exception ex) { Warn(ex, "unsubscribe"); }
        StopTimer();
        try { _timer.Dispose(); }
        catch (Exception ex) { Warn(ex, "dispose"); }
    }

    private void PostToUi(Action action)
    {
        // Stop can be requested from a non-UI caller. Cancellation takes effect
        // through epoch/disposed immediately; never wait for the UI to clean up.
        try
        {
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                _ = _dispatcher.BeginInvoke(DispatcherPriority.Send, action);
        }
        catch (Exception ex) { Warn(ex, "dispatch-cleanup"); }
    }

    private bool CheckAccess()
    {
        if (_dispatcher.CheckAccess()) return true;
        Warn(new InvalidOperationException("Preview access must stay on its UI thread."), "thread");
        return false;
    }

    private void StopTimer()
    {
        try { _timer.Stop(); }
        catch (Exception ex) { Warn(ex, "stop"); }
    }

    private static void Warn(Exception error, string operation)
    {
        try { Log.Warning(error, "PreviewFramePresenter: {Operation} failed", operation); }
        catch (Exception) { /* Preview diagnostics cannot interrupt external output. */ }
    }

    private sealed class PreviewDispatcherTimer : IPreviewFrameTimer
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
        public event Action? Tick;
        public bool IsEnabled => _timer.IsEnabled;
        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
        public PreviewDispatcherTimer() => _timer.Tick += HandleTick;
        private void HandleTick(object? sender, EventArgs e) => Tick?.Invoke();
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() { _timer.Stop(); _timer.Tick -= HandleTick; Tick = null; }
    }
}
