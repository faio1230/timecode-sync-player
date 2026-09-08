using System.Buffers;

namespace TimecodeSyncPlayer;

/// <summary>Owns a pooled copy; native rendering never writes these pixels after publication.</summary>
internal sealed class RenderedFrameSnapshot : IDisposable
{
    private byte[]? _pixels;
    private readonly ArrayPool<byte> _pool;
    private RenderedFrameSnapshot(byte[] pixels, int width, int height, int generation,
        long sequence, double renderMs, ArrayPool<byte> pool)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Generation = generation;
        Sequence = sequence;
        RenderMs = renderMs;
        _pool = pool;
    }

    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(RenderedFrameSnapshot));
    public int Width { get; }
    public int Height { get; }
    public int Generation { get; }
    public long Sequence { get; }
    public double RenderMs { get; }

    public static RenderedFrameSnapshot Copy(byte[] source, int width, int height,
        int generation, long sequence, double renderMs, ArrayPool<byte>? pool = null)
    {
        int size = FrameBufferSize.GetRequiredByteCount(width, height);
        if (source.Length < size) throw new ArgumentException("Incomplete rendered frame", nameof(source));
        pool ??= ArrayPool<byte>.Shared;
        byte[] pixels = pool.Rent(size);
        Array.Copy(source, pixels, size);
        return new RenderedFrameSnapshot(pixels, width, height, generation, sequence, renderMs, pool);
    }

    public void Dispose()
    {
        byte[]? pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels != null) _pool.Return(pixels);
    }
}

/// <summary>One pending frame, independent of any frame already leased by the UI.</summary>
internal sealed class LatestRenderedFrameMailbox : IDisposable
{
    private readonly object _sync = new();
    private RenderedFrameSnapshot? _latest;
    private bool _disposed;
    private readonly Action<RenderedFrameSnapshot, string>? _observe;

    public LatestRenderedFrameMailbox(Action<RenderedFrameSnapshot, string>? observe = null) => _observe = observe;

    private void DisposeObserved(RenderedFrameSnapshot? frame, string reason)
    {
        if (frame == null) return;
        try { _observe?.Invoke(frame, reason); }
        catch (Exception) { /* Diagnostics must not prevent returning pooled pixels. */ }
        frame.Dispose();
    }

    public void Publish(RenderedFrameSnapshot frame)
    {
        RenderedFrameSnapshot? replaced;
        lock (_sync)
        {
            replaced = _disposed ? frame : _latest;
            if (!_disposed) _latest = frame;
        }
        DisposeObserved(replaced, ReferenceEquals(replaced, frame) ? "mailbox-closed" : "mailbox-replaced");
    }

    public RenderedFrameSnapshot? Take()
    {
        lock (_sync)
        {
            var frame = _latest;
            _latest = null;
            return frame;
        }
    }

    public void Clear() => DisposeObserved(Take(), "mailbox-cleared");

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            DisposeObserved(_latest, "mailbox-disposed");
            _latest = null;
        }
    }
}
