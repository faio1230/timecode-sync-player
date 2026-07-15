using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public class LtcSignalPlayerTests
{
    [Fact]
    public void AdvanceTimecode_SkipsElapsedSilentFrames()
    {
        var start = new LtcTimecode(1, 2, 3, 20, false);

        LtcTimecode result = LtcSignalPlayer.AdvanceTimecode(start, fps: 25, frameCount: 45);

        result.Should().Be(new LtcTimecode(1, 2, 5, 15, false));
    }

    [Fact]
    public void AdvanceTimecode_WithZeroFramesReturnsStart()
    {
        var start = new LtcTimecode(23, 59, 59, 24, false);

        LtcSignalPlayer.AdvanceTimecode(start, fps: 25, frameCount: 0).Should().Be(start);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, -1)]
    public void AdvanceTimecode_WithInvalidArgumentThrows(int fps, int frameCount)
    {
        Action act = () => LtcSignalPlayer.AdvanceTimecode(
            new LtcTimecode(0, 0, 0, 0, false),
            fps,
            frameCount);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
