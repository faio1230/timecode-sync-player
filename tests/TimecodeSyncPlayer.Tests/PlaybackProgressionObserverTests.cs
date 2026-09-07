using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class PlaybackProgressionObserverTests
{
    [Fact]
    public void SyncRewindFromOldEof_RecognizesForwardProgressBelowOldPosition()
    {
        var observer = new PlaybackProgressionObserver();
        observer.Observe(19.967).Should().BeFalse();
        observer.Observe(5.733).Should().BeFalse();
        observer.Observe(5.833).Should().BeFalse();

        observer.Observe(5.933).Should().BeTrue();

        observer.Positions.Should().Equal(5.733, 5.833, 5.933);
    }

    [Fact]
    public void Rewind_DiscardsEarlierProgressBeforeRequiringThreeNewSamples()
    {
        var observer = new PlaybackProgressionObserver();
        observer.Observe(1).Should().BeFalse();
        observer.Observe(1.1).Should().BeFalse();
        observer.Observe(0.9).Should().BeFalse();

        observer.Observe(1.12).Should().BeFalse("only two increasing observations belong to the new run");
        observer.Observe(1.22).Should().BeTrue();
        observer.Positions.Should().Equal(0.9, 1.12, 1.22);
    }

    [Fact]
    public void RewindFollowedByStationaryPlayback_DoesNotCountAsProgress()
    {
        var observer = new PlaybackProgressionObserver();
        foreach (double position in new[] { 20d, 5d, 5d, 5d, 5d })
            observer.Observe(position).Should().BeFalse();
    }

    [Fact]
    public void DuplicateSamples_DoNotCountTowardThreeForwardObservations()
    {
        var observer = new PlaybackProgressionObserver();
        observer.Observe(5).Should().BeFalse();
        observer.Observe(5).Should().BeFalse();
        observer.Observe(5.1).Should().BeFalse();
        observer.Observe(5.1).Should().BeFalse();
        observer.Observe(5.2).Should().BeTrue();
        observer.Positions.Should().Equal(5, 5.1, 5.2);
    }
}
