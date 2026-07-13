using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimecodeFpsSelectorTests
{
    [Fact]
    public void Resolve_UsesFixedFps_WhenModeIsFixed()
    {
        var selector = new TimecodeFpsSelector();

        double fps = selector.Resolve(TimecodeFpsMode.Fixed25, detectedFps: 30.0, dropFrame: false);

        fps.Should().Be(25.0);
    }

    [Fact]
    public void Resolve_LocksAutoFps_AfterConsecutiveDetections()
    {
        var selector = new TimecodeFpsSelector(confirmCount: 3, changeCount: 8);

        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false).Should().Be(0.0);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false).Should().Be(0.0);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false).Should().Be(30.0);
    }

    [Fact]
    public void Resolve_IgnoresShortMixedDetections_AfterAutoFpsIsLocked()
    {
        var selector = new TimecodeFpsSelector(confirmCount: 3, changeCount: 8);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false);

        selector.Resolve(TimecodeFpsMode.Auto, 24.0, dropFrame: false).Should().Be(30.0);
        selector.Resolve(TimecodeFpsMode.Auto, 25.0, dropFrame: false).Should().Be(30.0);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false).Should().Be(30.0);
    }

    [Fact]
    public void Resolve_ChangesAutoFps_AfterRepeatedDifferentDetections()
    {
        var selector = new TimecodeFpsSelector(confirmCount: 3, changeCount: 3);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false);
        selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: false);

        selector.Resolve(TimecodeFpsMode.Auto, 24.0, dropFrame: false).Should().Be(30.0);
        selector.Resolve(TimecodeFpsMode.Auto, 24.0, dropFrame: false).Should().Be(30.0);
        selector.Resolve(TimecodeFpsMode.Auto, 24.0, dropFrame: false).Should().Be(24.0);
    }

    [Fact]
    public void Resolve_ConvertsDropFrameThirtyToTwentyNinePointNineSeven()
    {
        var selector = new TimecodeFpsSelector(confirmCount: 1, changeCount: 8);

        double fps = selector.Resolve(TimecodeFpsMode.Auto, 30.0, dropFrame: true);

        fps.Should().BeApproximately(30000.0 / 1001.0, 0.0001);
    }
}
