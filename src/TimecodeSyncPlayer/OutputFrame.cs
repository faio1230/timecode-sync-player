using System.Buffers;

namespace TimecodeSyncPlayer;

internal enum OutputFrameKind { Normal, Black, Frozen, Buffered }

/// <summary>
/// An immutable BGR0 image with its own lease. Every consumer must finish before
/// Dispose; consumers must not mutate PixelArray or retain pointers after a call.
/// </summary>
internal sealed class OutputFrame : IDisposable
{
    private byte[]? _pixels;
    private Action? _release;

    private OutputFrame(byte[] pixels, int width, int height, OutputFrameKind kind, Action release,
        int? generation = null, long? sequence = null, double? renderMs = null)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        Kind = kind;
        _release = release;
        Generation = generation;
        Sequence = sequence;
        RenderMs = renderMs;
    }

    internal byte[] PixelArray => _pixels ?? throw new ObjectDisposedException(nameof(OutputFrame));
    public ReadOnlyMemory<byte> Pixels => PixelArray.AsMemory(0, FrameBufferSize.GetRequiredByteCount(Width, Height));
    public int Width { get; }
    public int Height { get; }
    public OutputFrameKind Kind { get; }
    public string DiagnosticKind => Kind switch
    {
        OutputFrameKind.Normal => "normal", OutputFrameKind.Black => "black",
        OutputFrameKind.Frozen => "frozen", _ => "buffered"
    };
    public int? Generation { get; }
    public long? Sequence { get; }
    public double? RenderMs { get; }

    public static OutputFrame FromSnapshot(RenderedFrameSnapshot snapshot)
    {
        var lease = snapshot.Retain();
        return new OutputFrame(lease.Pixels, lease.Width, lease.Height, OutputFrameKind.Normal,
            lease.Dispose, lease.Generation, lease.Sequence, lease.RenderMs);
    }

    internal static OutputFrame Copy(byte[]? source, int width, int height, OutputFrameKind kind,
        ArrayPool<byte>? pool = null)
    {
        if (kind == OutputFrameKind.Normal) throw new ArgumentException("Normal frames require a native snapshot.", nameof(kind));
        int size = FrameBufferSize.GetRequiredByteCount(width, height);
        if (source != null && source.Length < size) throw new ArgumentException("Incomplete output frame", nameof(source));
        if (source == null && kind != OutputFrameKind.Black) throw new ArgumentNullException(nameof(source));
        pool ??= ArrayPool<byte>.Shared;
        byte[] pixels = pool.Rent(size);
        try
        {
            if (source == null) Array.Clear(pixels, 0, size);
            else Array.Copy(source, pixels, size);
            return new OutputFrame(pixels, width, height, kind, () => pool.Return(pixels));
        }
        catch { pool.Return(pixels); throw; }
    }

    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release == null) return;
        _pixels = null;
        release();
    }
}

/// <summary>
/// UI-owned image selection, independent of WPF and Spout. Frozen storage remains
/// the source image; future effects belong after selection, before publication.
/// </summary>
internal sealed class OutputFrameFactory(PixelBufferManager buffers)
{
    public OutputFrame Black(int width, int height)
    {
        var (w, h) = BlackFrameRenderPolicy.ResolveSize(width, height);
        return OutputFrame.Copy(null, w, h, OutputFrameKind.Black);
    }

    public OutputFrame? Frozen(int width, int height)
    {
        if (buffers.FrozenFrameBuffer == null || width <= 0 || height <= 0)
            return Black(width, height);
        if (!FrameBufferSize.TryGetRequiredByteCount(width, height, out int size)) return null;
        if (buffers.FrozenFrameBuffer.Length < size) return Black(width, height);
        return OutputFrame.Copy(buffers.FrozenFrameBuffer, width, height, OutputFrameKind.Frozen);
    }

    public OutputFrame? GapFreeze(int width, int height)
    {
        if (buffers.CachedGapFreezeFrameBuffer != null && buffers.CachedGapFreezeFrameWidth > 0 && buffers.CachedGapFreezeFrameHeight > 0)
            return Buffered(buffers.CachedGapFreezeFrameBuffer, buffers.CachedGapFreezeFrameWidth, buffers.CachedGapFreezeFrameHeight);
        return Frozen(width, height);
    }

    public OutputFrame? Buffered(byte[] source, int width, int height)
    {
        if (!FrameBufferSize.TryGetRequiredByteCount(width, height, out int size) || source.Length < size) return null;
        return OutputFrame.Copy(source, width, height, OutputFrameKind.Buffered);
    }
}
