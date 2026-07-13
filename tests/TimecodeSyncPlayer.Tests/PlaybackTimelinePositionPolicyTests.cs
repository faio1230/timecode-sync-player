using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaybackTimelinePositionPolicyTests
{
    [Fact]
    public void GetGapTimelinePosition_UsesLtcPositionOnlyInContinueGap()
    {
        PlaybackTimelinePositionPolicy.GetGapTimelinePosition(
            isGapInactive: false,
            syncMode: SyncMode.Continue,
            lastLtcSeconds: 123.4).Should().Be(123.4);

        PlaybackTimelinePositionPolicy.GetGapTimelinePosition(
            isGapInactive: false,
            syncMode: SyncMode.Single,
            lastLtcSeconds: 123.4).Should().BeNull();

        PlaybackTimelinePositionPolicy.GetGapTimelinePosition(
            isGapInactive: true,
            syncMode: SyncMode.Continue,
            lastLtcSeconds: 123.4).Should().BeNull();
    }

    [Fact]
    public void GetNormalTimelinePosition_UsesLtcPositionWhenContinueSyncIsEnabled()
    {
        double result = PlaybackTimelinePositionPolicy.GetNormalTimelinePosition(
            syncMode: SyncMode.Continue,
            syncEnabled: true,
            lastLtcSeconds: 100.0,
            playbackSeconds: 12.0);

        result.Should().Be(100.0);
    }

    [Theory]
    [InlineData(SyncMode.Single, true, 100.0)]
    [InlineData(SyncMode.Continue, false, 100.0)]
    [InlineData(SyncMode.Continue, true, 0.0)]
    public void GetNormalTimelinePosition_FallsBackToPlaybackPosition(
        SyncMode syncMode,
        bool syncEnabled,
        double lastLtcSeconds)
    {
        double result = PlaybackTimelinePositionPolicy.GetNormalTimelinePosition(
            syncMode,
            syncEnabled,
            lastLtcSeconds,
            playbackSeconds: 12.0);

        result.Should().Be(12.0);
    }
}
