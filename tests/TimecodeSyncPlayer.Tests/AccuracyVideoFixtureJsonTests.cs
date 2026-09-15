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
}
