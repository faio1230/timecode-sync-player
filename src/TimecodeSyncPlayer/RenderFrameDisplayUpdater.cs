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

        RecordFirstFrame(width, height);

        return bitmapMs;
    }

    internal (double BitmapMs, double SpoutMs) UpdateCombined(byte[] pixels, int width, int height, Func<double> send,
        Func<byte[], int, int, Func<double>, (double BitmapMs, double SpoutMs)> update)
    {
        var result = update(pixels, width, height, send);
        RecordFirstFrame(width, height); // Arbitrary logging callback stays outside the bitmap lock.
        return result;
    }

    private void RecordFirstFrame(int width, int height)
    {
        if (!_firstFrameDisplayedLogged)
        {
            _firstFrameDisplayedLogged = true;
            _logFirstFrame(width, height);
        }
    }

    public void Reset()
    {
        _firstFrameDisplayedLogged = false;
    }
}
