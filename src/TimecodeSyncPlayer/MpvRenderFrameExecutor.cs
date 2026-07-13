using System.Diagnostics;

namespace TimecodeSyncPlayer;

public sealed record MpvRenderFrameResult(int ReturnCode, double ElapsedMs);

public sealed class MpvRenderFrameExecutor
{
    private readonly Func<int> _render;

    public MpvRenderFrameExecutor(Func<int> render)
    {
        _render = render;
    }

    public MpvRenderFrameResult Render()
    {
        long started = Stopwatch.GetTimestamp();
        int rc = _render();
        return new MpvRenderFrameResult(rc, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
