using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// WriteableBitmap を保持し、各種レンダリングポリシーを実行する。
/// BitmapChanged イベントで呼び出し元が VideoImage.Source を更新する。
/// UIスレッド上でのみ呼び出すこと。
/// </summary>
internal sealed class FrameRenderer
{
    private readonly PixelBufferManager _bufferManager;
    private readonly ISpoutOutput _spoutOutput;
    private WriteableBitmap? _bitmap;
    private const double DefaultDpi = 96;

    public FrameRenderer(PixelBufferManager bufferManager, ISpoutOutput spoutOutput)
    {
        _bufferManager = bufferManager;
        _spoutOutput   = spoutOutput;
    }

    /// <summary>WriteableBitmap が新規作成またはリサイズされたときに発火する。</summary>
    public event Action<WriteableBitmap>? BitmapChanged;

    /// <summary>_bufferManager.PixelBuffer の内容を WriteableBitmap に書き込む（RenderFrame 用）。</summary>
    public void UpdateFromPixelBuffer(int w, int h)
    {
        if (!FrameBufferSize.TryGetRequiredByteCount(w, h, out int byteCount)) return;
        if (_bufferManager.PixelBuffer == null) return;
        EnsureBitmap(w, h);
        _bitmap!.Lock();
        try
        {
            byteCount = Math.Min(_bufferManager.PixelBuffer.Length, byteCount);
            Marshal.Copy(_bufferManager.PixelBuffer, 0, _bitmap.BackBuffer, byteCount);
            _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    /// <summary>黒フレームを描画して Spout 送信する。</summary>
    public void RenderBlack(int videoWidth, int videoHeight)
    {
        (int w, int h) = BlackFrameRenderPolicy.ResolveSize(videoWidth, videoHeight);
        _bufferManager.EnsurePixelBuffer(w, h);
        _bufferManager.ClearPixelBuffer();
        UpdateFromPixelBuffer(w, h);
        _spoutOutput.SendFrame(_bufferManager.PixelPtr, w, h);
    }

    /// <summary>FrozenFrameBuffer の内容を描画する。利用不可なら黒フレームにフォールバック。</summary>
    public void RenderFrozen(int videoWidth, int videoHeight)
    {
        if (_bufferManager.FrozenFrameBuffer == null || videoWidth <= 0 || videoHeight <= 0)
        {
            Log.Debug("Continue mode: frozen frame unavailable, rendering black frame");
            RenderBlack(videoWidth, videoHeight);
            return;
        }
        int w = videoWidth;
        int h = videoHeight;
        if (!FrameBufferSize.TryGetRequiredByteCount(w, h, out int frameNeeded)) return;
        if (_bufferManager.FrozenFrameBuffer!.Length < frameNeeded)
        {
            RenderBlack(videoWidth, videoHeight);
            return;
        }
        EnsureBitmap(w, h);
        _bitmap!.Lock();
        try
        {
            Marshal.Copy(_bufferManager.FrozenFrameBuffer, 0, _bitmap.BackBuffer, frameNeeded);
            _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
        }
        finally
        {
            _bitmap.Unlock();
        }
        _spoutOutput.SendFrame(_bufferManager.FrozenFramePtr, w, h);
    }

    /// <summary>CachedGapFreezeFrame があれば描画、なければ FrozenFrame にフォールバック。</summary>
    public void RenderGapFreeze(int videoWidth, int videoHeight)
    {
        if (_bufferManager.CachedGapFreezeFrameBuffer != null &&
            _bufferManager.CachedGapFreezeFrameWidth > 0 &&
            _bufferManager.CachedGapFreezeFrameHeight > 0)
        {
            RenderBuffered(
                _bufferManager.CachedGapFreezeFrameBuffer,
                _bufferManager.CachedGapFreezeFramePtr,
                _bufferManager.CachedGapFreezeFrameWidth,
                _bufferManager.CachedGapFreezeFrameHeight);
            return;
        }
        RenderFrozen(videoWidth, videoHeight);
    }

    /// <summary>任意のバイト配列を描画して Spout 送信する。</summary>
    public void RenderBuffered(byte[] buffer, IntPtr handle, int width, int height)
    {
        if (!FrameBufferSize.TryGetRequiredByteCount(width, height, out int frameNeeded)) return;
        if (buffer.Length < frameNeeded)
            return;
        EnsureBitmap(width, height);
        _bitmap!.Lock();
        try
        {
            Marshal.Copy(buffer, 0, _bitmap.BackBuffer, frameNeeded);
            _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, width, height));
        }
        finally
        {
            _bitmap.Unlock();
        }
        if (handle != IntPtr.Zero)
            _spoutOutput.SendFrame(handle, width, height);
    }

    private void EnsureBitmap(int w, int h)
    {
        if (_bitmap != null && _bitmap.PixelWidth == w && _bitmap.PixelHeight == h) return;
        _bitmap = new WriteableBitmap(w, h, DefaultDpi, DefaultDpi, PixelFormats.Bgr32, null);
        BitmapChanged?.Invoke(_bitmap);
    }
}
