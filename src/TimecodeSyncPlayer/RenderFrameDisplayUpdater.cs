using System.Diagnostics;

namespace TimecodeSyncPlayer;

public sealed class RenderFrameDisplayUpdater
{
    private readonly Action<byte[], int, int> _updateBitmap;
    private readonly Action<int, int> _logFirstFrame;
    private bool _firstFrameDisplayedLogged;

    public RenderFrameDisplayUpdater(Action<byte[], int, int> updateBitmap, Action<int, int> logFirstFrame)
    {
        _updateBitmap = updateBitmap;
        _logFirstFrame = logFirstFrame;
    }

    public double Update(byte[] pixels, int width, int height)
    {
        long started = Stopwatch.GetTimestamp();
        _updateBitmap(pixels, width, height);
        double bitmapMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (!_firstFrameDisplayedLogged)
        {
            _firstFrameDisplayedLogged = true;
            _logFirstFrame(width, height);
        }

        return bitmapMs;
    }

    public void Reset()
    {
        _firstFrameDisplayedLogged = false;
    }
}
