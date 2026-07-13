using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaybackTimeFormatterTests
{
    [Fact]
    public void FormatFrames_FormatsHoursMinutesSecondsAndFrames()
    {
        string result = PlaybackTimeFormatter.FormatFrames(3661.5, fps: 30.0);

        result.Should().Be("1:01:01:15");
    }

    [Fact]
    public void FormatFrames_ClampsNegativeSeconds()
    {
        string result = PlaybackTimeFormatter.FormatFrames(-1.0, fps: 30.0);

        result.Should().Be("0:00:00:00");
    }

    [Fact]
    public void FormatFrames_UsesZeroFramesWhenFpsIsUnavailable()
    {
        string result = PlaybackTimeFormatter.FormatFrames(12.75, fps: 0.0);

        result.Should().Be("0:00:12:00");
    }
}
