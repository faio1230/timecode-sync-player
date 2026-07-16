using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer;

public sealed class PixelBufferManager : IDisposable
{
    private byte[]? _pixelBuffer;
    private GCHandle _pixelHandle;
    private byte[]? _frozenFrameBuffer;
    private GCHandle _frozenFrameHandle;
    private byte[]? _cachedGapFreezeFrameBuffer;
    private GCHandle _cachedGapFreezeFrameHandle;
    private int _cachedGapFreezeFrameWidth;
    private int _cachedGapFreezeFrameHeight;

    private IntPtr _fmtStr = IntPtr.Zero;
    private IntPtr _stridePtr = IntPtr.Zero;
    private int[] _sizeArr = new int[2];
    private GCHandle _sizeHandle;
    private bool _disposed;

    public PixelBufferManager()
    {
        _sizeHandle = GCHandle.Alloc(_sizeArr, GCHandleType.Pinned);
    }

    public IntPtr FormatStringPtr => _fmtStr;
    public IntPtr StridePtr => _stridePtr;
    public IntPtr SizeArrayPtr => _sizeHandle.AddrOfPinnedObject();
    public int[] SizeArray => _sizeArr;
    public IntPtr PixelPtr => _pixelHandle.IsAllocated ? _pixelHandle.AddrOfPinnedObject() : IntPtr.Zero;
    public IntPtr FrozenFramePtr => _frozenFrameHandle.IsAllocated ? _frozenFrameHandle.AddrOfPinnedObject() : IntPtr.Zero;
    public byte[]? PixelBuffer => _pixelBuffer;
    public byte[]? FrozenFrameBuffer => _frozenFrameBuffer;
    public byte[]? CachedGapFreezeFrameBuffer => _cachedGapFreezeFrameBuffer;
    public IntPtr CachedGapFreezeFramePtr => _cachedGapFreezeFrameHandle.IsAllocated ? _cachedGapFreezeFrameHandle.AddrOfPinnedObject() : IntPtr.Zero;
    public int CachedGapFreezeFrameWidth => _cachedGapFreezeFrameWidth;
    public int CachedGapFreezeFrameHeight => _cachedGapFreezeFrameHeight;

    public void InitFormatString(string format)
    {
        if (_fmtStr != IntPtr.Zero) Marshal.FreeHGlobal(_fmtStr);
        _fmtStr = Marshal.StringToHGlobalAnsi(format);
    }

    public void InitStridePtr()
    {
        if (_stridePtr != IntPtr.Zero) Marshal.FreeHGlobal(_stridePtr);
        _stridePtr = Marshal.AllocHGlobal(8);
    }

    public void EnsurePixelBuffer(int width, int height)
    {
        int needed = FrameBufferSize.GetRequiredByteCount(width, height);
        if (_pixelBuffer == null || _pixelBuffer.Length < needed)
        {
            if (_pixelHandle.IsAllocated) _pixelHandle.Free();
            _pixelBuffer = new byte[needed];
            _pixelHandle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
        }
    }

    public void EnsureFrozenFrameBuffer(int width, int height)
    {
        int needed = FrameBufferSize.GetRequiredByteCount(width, height);
        if (_frozenFrameBuffer == null || _frozenFrameBuffer.Length < needed)
        {
            if (_frozenFrameHandle.IsAllocated) _frozenFrameHandle.Free();
            _frozenFrameBuffer = new byte[needed];
            _frozenFrameHandle = GCHandle.Alloc(_frozenFrameBuffer, GCHandleType.Pinned);
        }
    }

    public void EnsureGapFreezeFrameBuffer(int width, int height)
    {
        int needed = FrameBufferSize.GetRequiredByteCount(width, height);
        if (_cachedGapFreezeFrameBuffer == null || _cachedGapFreezeFrameBuffer.Length < needed)
        {
            if (_cachedGapFreezeFrameHandle.IsAllocated) _cachedGapFreezeFrameHandle.Free();
            _cachedGapFreezeFrameBuffer = new byte[needed];
            _cachedGapFreezeFrameHandle = GCHandle.Alloc(_cachedGapFreezeFrameBuffer, GCHandleType.Pinned);
        }
    }

    public void CopyToFrozenFrame(int width, int height)
    {
        if (!FrameBufferSize.TryGetRequiredByteCount(width, height, out int needed)) return;
        if (_pixelBuffer == null) return;
        if (_frozenFrameBuffer == null || _frozenFrameBuffer.Length < needed) return;
        Array.Copy(_pixelBuffer, _frozenFrameBuffer, needed);
    }

    public void CopyFrozenToGapFreezeFrame(int width, int height)
    {
        if (!FrameBufferSize.TryGetRequiredByteCount(width, height, out int needed)) return;
        if (_frozenFrameBuffer == null) return;
        if (_frozenFrameBuffer.Length < needed) return;
        EnsureGapFreezeFrameBuffer(width, height);
        Array.Copy(_frozenFrameBuffer, _cachedGapFreezeFrameBuffer!, needed);
        _cachedGapFreezeFrameWidth = width;
        _cachedGapFreezeFrameHeight = height;
    }

    public void ClearGapFreezeFrame()
    {
        if (_cachedGapFreezeFrameHandle.IsAllocated)
            _cachedGapFreezeFrameHandle.Free();
        _cachedGapFreezeFrameBuffer = null;
        _cachedGapFreezeFrameWidth = 0;
        _cachedGapFreezeFrameHeight = 0;
    }

    public void ClearPixelBuffer()
    {
        if (_pixelBuffer != null)
            Array.Clear(_pixelBuffer, 0, _pixelBuffer.Length);
    }

    public void ClearFrozenFrame()
    {
        if (_frozenFrameHandle.IsAllocated) _frozenFrameHandle.Free();
        _frozenFrameBuffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pixelHandle.IsAllocated) _pixelHandle.Free();
        if (_frozenFrameHandle.IsAllocated) _frozenFrameHandle.Free();
        if (_cachedGapFreezeFrameHandle.IsAllocated) _cachedGapFreezeFrameHandle.Free();
        if (_sizeHandle.IsAllocated) _sizeHandle.Free();
        if (_fmtStr != IntPtr.Zero) Marshal.FreeHGlobal(_fmtStr);
        if (_stridePtr != IntPtr.Zero) Marshal.FreeHGlobal(_stridePtr);
    }
}
