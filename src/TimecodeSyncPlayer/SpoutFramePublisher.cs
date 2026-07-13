using System.Diagnostics;

namespace TimecodeSyncPlayer;

public sealed class SpoutFramePublisher
{
    private readonly ISpoutOutput _spoutOutput;

    public SpoutFramePublisher(ISpoutOutput spoutOutput)
    {
        _spoutOutput = spoutOutput;
    }

    public double Publish(IntPtr pixels, int width, int height)
    {
        long started = Stopwatch.GetTimestamp();
        _spoutOutput.SendFrame(pixels, width, height);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
