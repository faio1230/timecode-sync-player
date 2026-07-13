using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class MpvRenderFrameExecutorTests
{
    [Fact]
    public void Render_ReturnsResultCodeAndElapsedMilliseconds()
    {
        var executor = new MpvRenderFrameExecutor(() => -1);

        MpvRenderFrameResult result = executor.Render();

        result.ReturnCode.Should().Be(-1);
        result.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
    }
}
