using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class ScanoutTrackerTests
{
    [Fact]
    public void Reset_ClearsCountsForARecreatedSwapchain()
    {
        var tracker = new ScanoutTracker(4);
        tracker.Record(412, 1, 2, 3);
        tracker.Reset();
        tracker.LastRecordedPresentCount.Should().Be(0);
        tracker.LastObservedPresentCount.Should().Be(0);
        tracker.Recorded.Should().Be(0);
        tracker.PendingCount.Should().Be(0);
        tracker.Record(1, 4, 5, 6); // 新 swapchain の PresentCount=1 を受け入れる
        tracker.LastRecordedPresentCount.Should().Be(1);
    }

    [Fact]
    public void Observe_AdvancesOncePerPresentCountAndSuppressesDuplicates()
    {
        var tracker = new ScanoutTracker(16);
        tracker.Observe(0, 0, 0, 0).Should().BeNull();
        tracker.Observe(0, 5, 5, 500).Should().BeNull();
        tracker.Record(1, 11, 1100, 1200);
        tracker.PendingCount.Should().Be(1);
        var seen = tracker.Observe(1, 40, 40, 9000);
        seen.Should().Be(new ScanoutObservation(1, 40, 40, 9000, 11, 1100, 1200, true, 0));
        seen!.Value.Detail.Should().Be("1:40");
        tracker.PendingCount.Should().Be(0);
        tracker.Observed.Should().Be(1);
        tracker.Observe(1, 41, 41, 9100).Should().BeNull();
        tracker.Observe(1, 42, 42, 9200).Should().BeNull();
        tracker.Observed.Should().Be(1);
        tracker.LastObservedPresentCount.Should().Be(1);
        FluentActions.Invoking(() => tracker.Record(1, 12, 1300, 1400)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => tracker.Record(0, 12, 1300, 1400)).Should().Throw<InvalidOperationException>();
        tracker.Record(2, 12, 1300, 1400);
        tracker.Observe(2, 41, 41, 9100).Should().Match<ScanoutObservation?>(s => s!.Value.Mapped && s.Value.ImageId == 12);
    }

    [Fact]
    public void Observe_JumpReportsLatestAndLeavesSkippedPending()
    {
        var tracker = new ScanoutTracker(16);
        for (uint count = 1; count <= 4; count++) tracker.Record(count, 10 + count, 1000 * count, 1000 * count + 5);
        var seen = tracker.Observe(4, 70, 70, 70_000);
        seen.Should().Match<ScanoutObservation?>(s => s!.Value.Mapped && s.Value.ImageId == 14 && s.Value.Skipped == 3);
        tracker.PendingCount.Should().Be(3);
        tracker.Observed.Should().Be(1);
        tracker.Recorded.Should().Be(4);
        tracker.Observe(3, 71, 71, 71_000).Should().BeNull();
        tracker.Observe(2, 71, 71, 71_000).Should().BeNull();
        tracker.Record(5, 15, 5000, 5005);
        tracker.Record(6, 16, 6000, 6005);
        var next = tracker.Observe(6, 72, 72, 72_000);
        next.Should().Match<ScanoutObservation?>(s => s!.Value.Mapped && s.Value.ImageId == 16 && s.Value.Skipped == 1);
        tracker.PendingCount.Should().Be(4);
    }

    [Fact]
    public void Observe_UnknownOrEvictedPresentCountIsUnmapped()
    {
        var tracker = new ScanoutTracker(16);
        var unknown = tracker.Observe(7, 30, 30, 3000);
        unknown.Should().Match<ScanoutObservation?>(s => !s!.Value.Mapped && s.Value.ImageId == 0 && s.Value.PresentCount == 7 && s.Value.Skipped == 0);
        unknown!.Value.Detail.Should().Be("7:30:unmapped");
        tracker.PendingCount.Should().Be(0);

        for (uint count = 8; count < 8 + 17; count++) tracker.Record(count, count, count * 10, count * 10 + 1);
        var evicted = tracker.Observe(8, 31, 31, 3100);
        evicted.Should().Match<ScanoutObservation?>(s => !s!.Value.Mapped && s.Value.Skipped == 0);
        evicted!.Value.Detail.Should().Be("8:31:unmapped");
        tracker.PendingCount.Should().Be(17);
        tracker.Observe(9, 32, 32, 3200).Should().Match<ScanoutObservation?>(s => s!.Value.Mapped && s.Value.ImageId == 9);
        tracker.PendingCount.Should().Be(16);
        var beyond = tracker.Observe(30, 40, 40, 4000);
        beyond.Should().Match<ScanoutObservation?>(s => !s!.Value.Mapped && s.Value.Skipped == 15);
        tracker.PendingCount.Should().Be(16);
    }

    [Fact]
    public void NoteDisjoint_IsRecordedOnce()
    {
        var tracker = new ScanoutTracker(16);
        tracker.Disjoint.Should().BeFalse();
        tracker.NoteDisjoint().Should().BeTrue();
        tracker.Disjoint.Should().BeTrue();
        tracker.NoteDisjoint().Should().BeFalse();
        tracker.NoteDisjoint().Should().BeFalse();
        tracker.Record(1, 1, 10, 11);
        tracker.Observe(1, 1, 1, 100).Should().Match<ScanoutObservation?>(s => s!.Value.Mapped);
        tracker.Disjoint.Should().BeTrue();
        FluentActions.Invoking(() => new ScanoutTracker(0).Record(1, 1, 1, 1)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
