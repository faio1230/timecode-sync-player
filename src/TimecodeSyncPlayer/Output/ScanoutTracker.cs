using System.Globalization;

namespace TimecodeSyncPlayer.Output;

// 1件の DXGI フレーム統計観測を、記録済み Present と突き合わせた結果。Unmapped は表に無い PresentCount。
internal readonly record struct ScanoutObservation(uint PresentCount, uint PresentRefreshCount, uint SyncRefreshCount, long SyncQpcTime,
    long ImageId, long GeneratedQpc, long PresentStartQpc, bool Mapped, int Skipped)
{
    public string Detail => PresentCount.ToString(CultureInfo.InvariantCulture) + ":" + PresentRefreshCount.ToString(CultureInfo.InvariantCulture) + (Mapped ? "" : ":unmapped");
}

/// <summary>
/// GPU worker のみが所有する presentCount → (画像, 生成時刻, present.start) の表。DXGI 呼び出しはしない。
/// Observe は進んだ PresentCount につき最大1件を返し、飛ばされた Present は未観測のまま残る。
/// 試作 scripts/GpuOutputProbe の ScanoutTracker を移植。
/// </summary>
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
        while (entries.Count > 0 && entries[0].Count <= presentCount)
        {
            if (entries[0].Count == presentCount) found = entries[0]; else skipped++;
            entries.RemoveAt(0);
        }
        if (found is { } f) { Observed++; return new(presentCount, presentRefreshCount, syncRefreshCount, syncQpcTime, f.ImageId, f.GeneratedQpc, f.PresentStartQpc, true, skipped); }
        return new(presentCount, presentRefreshCount, syncRefreshCount, syncQpcTime, 0, 0, 0, false, skipped);
    }

    // 最初の disjoint 報告だけ true を返し、呼び出し側は1回だけ記録する。
    public bool NoteDisjoint() { if (Disjoint) return false; Disjoint = true; return true; }

    /// <summary>
    /// swapchain の作り直しで PresentCount が 1 に戻るため、追跡状態を捨てる。
    /// 全画面の接続・デバイス復旧後の再作成で呼ぶ。
    /// </summary>
    public void Reset()
    {
        entries.Clear();
        LastRecordedPresentCount = 0;
        LastObservedPresentCount = 0;
        Recorded = 0;
        Observed = 0;
        Disjoint = false;
    }
}
