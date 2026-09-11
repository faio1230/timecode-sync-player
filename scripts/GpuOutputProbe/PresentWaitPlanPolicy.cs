namespace GpuOutputProbe;

internal sealed record PresentWaitSegment(int Index, int WaitMs, double StartSeconds, double EndSeconds,
    double AnalysisStartSeconds, double AnalysisEndSeconds, long StartQpc, long EndQpc);

internal static class PresentWaitPlanPolicy
{
    public static PresentWaitSegment[] Create(Options options, long origin, long frequency)
    {
        int[] waits = options.PresentWaitPlan switch
        {
            "fixed" => [options.PresentWaitMs],
            "abba" => [0, 1, 1, 0],
            "baab" => [1, 0, 0, 1],
            _ => throw new ArgumentException("Unknown presentation wait plan.")
        };
        double duration = options.Seconds / waits.Length;
        return Enumerable.Range(0, waits.Length).Select(i => new PresentWaitSegment(i, waits[i],
            i * duration, (i + 1) * duration, i * duration + options.Warmup, (i + 1) * duration - options.Warmup,
            origin + (long)(i * duration * frequency), origin + (long)((i + 1) * duration * frequency))).ToArray();
    }

    public static PresentWaitSegment Select(IReadOnlyList<PresentWaitSegment> segments, long scheduledQpc)
    {
        foreach (var segment in segments)
            if (scheduledQpc >= segment.StartQpc && scheduledQpc < segment.EndQpc) return segment;
        throw new ArgumentOutOfRangeException(nameof(scheduledQpc), "Scheduled tick lies outside the configured run.");
    }
}

// Tracks only the applied segment index. Equal-valued neighbors still represent separate measurement segments.
internal sealed class PresentWaitPlanCursor(IReadOnlyList<PresentWaitSegment> segments)
{
    private int lastIndex = -1;
    public PresentWaitSegment Select(long scheduledQpc, out bool changed)
    {
        var selected = PresentWaitPlanPolicy.Select(segments, scheduledQpc);
        changed = selected.Index != lastIndex;
        lastIndex = selected.Index;
        return selected;
    }
}
