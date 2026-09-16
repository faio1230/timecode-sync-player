using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public class AccuracyVideoFixtureJsonTests
{
    [Theory]
    [InlineData(25.0)]
    [InlineData(30000.0 / 1001.0)]
    public void BuildFixtureJson_ParameterizesLtcFps(double ltcFps)
    {
        string json = AccuracyVideoFixture.BuildFixtureJson(ltcFps, Array.Empty<AccuracyClip>());

        json.Should().Contain("\"schema\":1");
        json.Should().Contain("\"ltcFps\":");
        json.Should().Contain(ltcFps.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void BuildFixtureJson_RecordsKeyframeIntervalPerClip()
    {
        var clip = new AccuracyClip(3, "accuracy-3", 60, 1, 720, 24, 0, 10, "C:/clip-3.mp4", 60, 1.0);

        string json = AccuracyVideoFixture.BuildFixtureJson(25.0, new[] { clip });

        json.Should().Contain("\"keyframeIntervalFrames\":60");
        json.Should().Contain("\"keyframeIntervalSeconds\":1");
    }
}
