using System.Globalization;

namespace GpuOutputProbe;

// One DXGI frame-statistics observation resolved against the recorded presents. Unmapped: PresentCount not in the table.
internal readonly record struct ScanoutObservation(uint PresentCount, uint PresentRefreshCount, uint SyncRefreshCount, long SyncQpcTime,
    long ImageId, long GeneratedQpc, long PresentStartQpc, bool Mapped, int Skipped)
{
    public string Detail => PresentCount.ToString(CultureInfo.InvariantCulture) + ":" + PresentRefreshCount.ToString(CultureInfo.InvariantCulture) + (Mapped ? "" : ":unmapped");
}

// Pure GPU-worker-owned map presentCount -> (image, generated, present.start). No DXGI call is made here.
// Observe emits at most one record per advanced PresentCount; presents jumped over stay unobserved (pending).
internal sealed class ScanoutTracker(int capacity = 16)
{
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly List<(uint Count, long ImageId, long GeneratedQpc, long PresentStartQpc)> entries = new();
    public uint LastRecordedPresentCount { get; private set; }
    public uint LastObservedPresentCount { get; private set; }
    public int Recorded { get; private set; }
    public int Observed { get; private set; }
    public int PendingCount => Recorded - Observed;
    public bool Disjoint { get; private set; }
    public void Record(uint presentCount, long imageId, long generatedQpc, long presentStartQpc)
    {
        if (presentCount <= LastRecordedPresentCount) throw new InvalidOperationException($"Present count {presentCount} does not exceed {LastRecordedPresentCount}.");
        LastRecordedPresentCount = presentCount; Recorded++;
        if (entries.Count == capacity) entries.RemoveAt(0);
        entries.Add((presentCount, imageId, generatedQpc, presentStartQpc));
    }
    public ScanoutObservation? Observe(uint presentCount, uint presentRefreshCount, uint syncRefreshCount, long syncQpcTime)
    {
        if (presentCount <= LastObservedPresentCount) return null;
        LastObservedPresentCount = presentCount;
        int skipped = 0; (uint Count, long ImageId, long GeneratedQpc, long PresentStartQpc)? found = null;
        // Entries at or below the observed count can never be reported later: the statistics count is monotonic.
        while (entries.Count > 0 && entries[0].Count <= presentCount)
        {
            if (entries[0].Count == presentCount) found = entries[0]; else skipped++;
            entries.RemoveAt(0);
        }
        if (found is { } f) { Observed++; return new(presentCount, presentRefreshCount, syncRefreshCount, syncQpcTime, f.ImageId, f.GeneratedQpc, f.PresentStartQpc, true, skipped); }
        return new(presentCount, presentRefreshCount, syncRefreshCount, syncQpcTime, 0, 0, 0, false, skipped);
    }
    // True only for the first disjoint report, so the caller records the condition once.
    public bool NoteDisjoint() { if (Disjoint) return false; Disjoint = true; return true; }
}
