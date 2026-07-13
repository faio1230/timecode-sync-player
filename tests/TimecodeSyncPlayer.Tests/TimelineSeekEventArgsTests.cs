using FluentAssertions;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

public class TimelineSeekEventArgsTests
{
    [Fact]
    public void Constructor_ShouldSetProperties()
    {
        var args = new TimelineSeekEventArgs(123.456, 2);
        args.TargetSeconds.Should().Be(123.456);
        args.TrackIndex.Should().Be(2);
    }
}
