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
        // GPU フレームソースが利用可能なら CPU 画素送信でなく GPU 画像を送る
        // （mpv 経路の SpoutOutput は非実装なので従来動作のまま）。
        if (_spoutOutput is not IGpuSpoutPublisher gpu || !gpu.TryPublishCurrentGpuFrame())
            _spoutOutput.SendFrame(pixels, width, height);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
