using System.Diagnostics;

namespace TimecodeSyncPlayer;

public sealed class RenderFrameDisplayUpdater
{
    private readonly Action<int, int> _updateBitmap;
    private readonly Action<int, int> _logFirstFrame;
    private bool _firstFrameDisplayedLogged;

    public RenderFrameDisplayUpdater(Action<int, int> updateBitmap, Action<int, int> logFirstFrame)
    {
        _updateBitmap = updateBitmap;
        _logFirstFrame = logFirstFrame;
    }

    public double Update(int width, int height)
    {
        long started = Stopwatch.GetTimestamp();
        _updateBitmap(width, height);
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
