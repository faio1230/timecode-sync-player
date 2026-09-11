namespace GpuOutputProbe;

// Fake statistics only: no swapchain or DXGI call is made here.
internal static class ScanoutSelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public static void AdvanceAndDuplicateSuppression()
    {
        var tracker = new ScanoutTracker(16);
        Check(tracker.Observe(0, 0, 0, 0) == null && tracker.Observe(0, 5, 5, 500) == null, "Zero statistics before any present produced a record.");
        tracker.Record(1, 11, 1100, 1200);
        Check(tracker.PendingCount == 1, "Recorded present is not pending.");
        var seen = tracker.Observe(1, 40, 40, 9000);
        Check(seen is { Mapped: true, ImageId: 11, GeneratedQpc: 1100, PresentStartQpc: 1200, PresentCount: 1, PresentRefreshCount: 40, SyncRefreshCount: 40, SyncQpcTime: 9000, Skipped: 0 }, "Mapped observation differs.");
        Check(seen!.Value.Detail == "1:40", "Detail format differs: " + seen.Value.Detail);
        Check(tracker.PendingCount == 0 && tracker.Observed == 1, "Observed present still pending.");
        Check(tracker.Observe(1, 41, 41, 9100) == null && tracker.Observe(1, 42, 42, 9200) == null, "Same PresentCount was recorded twice.");
        Check(tracker.Observed == 1 && tracker.LastObservedPresentCount == 1, "Duplicate observation changed state.");
        Throws<InvalidOperationException>(() => tracker.Record(1, 12, 1300, 1400));
        Throws<InvalidOperationException>(() => tracker.Record(0, 12, 1300, 1400));
        tracker.Record(2, 12, 1300, 1400);
        Check(tracker.Observe(2, 41, 41, 9100) is { Mapped: true, ImageId: 12 }, "Next present was not observed.");
    }

    public static void JumpLeavesSkippedPending()
    {
        var tracker = new ScanoutTracker(16);
        for (uint count = 1; count <= 4; count++) tracker.Record(count, 10 + count, 1000 * count, 1000 * count + 5);
        var seen = tracker.Observe(4, 70, 70, 70_000);
        Check(seen is { Mapped: true, ImageId: 14, Skipped: 3 }, "Jump did not report only the latest present with skipped count.");
        Check(tracker.PendingCount == 3 && tracker.Observed == 1 && tracker.Recorded == 4, "Skipped presents were not left pending.");
        Check(tracker.Observe(3, 71, 71, 71_000) == null && tracker.Observe(2, 71, 71, 71_000) == null, "An older PresentCount was recorded after a newer one.");
        tracker.Record(5, 15, 5000, 5005); tracker.Record(6, 16, 6000, 6005);
        var next = tracker.Observe(6, 72, 72, 72_000);
        Check(next is { Mapped: true, ImageId: 16, Skipped: 1 } && tracker.PendingCount == 4, "Second jump miscounted skipped presents.");
    }

    public static void UnmappedAndCapacity()
    {
        var tracker = new ScanoutTracker(16);
        var unknown = tracker.Observe(7, 30, 30, 3000);
        Check(unknown is { Mapped: false, ImageId: 0, GeneratedQpc: 0, PresentStartQpc: 0, PresentCount: 7, Skipped: 0 }, "Unmapped observation differs.");
        Check(unknown!.Value.Detail == "7:30:unmapped", "Unmapped detail differs: " + unknown.Value.Detail);
        Check(tracker.PendingCount == 0, "Unmapped observation changed pending count.");
        for (uint count = 8; count < 8 + 17; count++) tracker.Record(count, count, count * 10, count * 10 + 1); // 17 presents, capacity 16: count 8 is evicted.
        var evicted = tracker.Observe(8, 31, 31, 3100);
        Check(evicted is { Mapped: false, Skipped: 0 } && evicted!.Value.Detail == "8:31:unmapped", "Evicted present was not reported as unmapped.");
        Check(tracker.PendingCount == 17, "Evicted present must stay unobserved.");
        Check(tracker.Observe(9, 32, 32, 3200) is { Mapped: true, ImageId: 9 } && tracker.PendingCount == 16, "Oldest retained present was not observable.");
        // A present count beyond every recorded present (e.g. a Present with a non-S_OK status was not recorded) is unmapped and skips the rest.
        var beyond = tracker.Observe(30, 40, 40, 4000);
        Check(beyond is { Mapped: false, Skipped: 15 } && tracker.PendingCount == 16, "Unrecorded present beyond the table differs.");
    }

    public static void DisjointRecordedOnce()
    {
        var tracker = new ScanoutTracker(16);
        Check(!tracker.Disjoint, "Disjoint before any report.");
        Check(tracker.NoteDisjoint() && tracker.Disjoint, "First disjoint was not reported.");
        Check(!tracker.NoteDisjoint() && !tracker.NoteDisjoint() && tracker.Disjoint, "Disjoint was reported more than once.");
        tracker.Record(1, 1, 10, 11);
        Check(tracker.Observe(1, 1, 1, 100) is { Mapped: true } && tracker.Disjoint, "Observation after disjoint failed or cleared the flag.");
        Throws<ArgumentOutOfRangeException>(() => new ScanoutTracker(0).Record(1, 1, 1, 1));
    }
}
