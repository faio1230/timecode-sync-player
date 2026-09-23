using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>出力トレース（events.jsonl）の 1 件のうち、この監査で使う項目だけ。</summary>
internal readonly record struct TraceEvent(string Stage, long Qpc, long ImageId, string? Detail);

/// <summary>
/// 「同じ絵が続いた区間」1 つ。<see cref="DeliveriesDuring"/> が 0 なら shim から新しいフレームが
/// 来ていなかった（再生側が止まっていた）、1 以上なら**フレームは届いていたのに合成が採らなかった**。
/// </summary>
internal readonly record struct HeldSpan(long StartQpc, long EndQpc, double Seconds, int Ticks, int DeliveriesDuring);

internal readonly record struct DeliveryGap(long StartQpc, long EndQpc, double Seconds);

internal sealed record OutputContinuitySummary(
    int ComposeTicks,
    int Deliveries,
    IReadOnlyList<HeldSpan> HeldSpans,
    IReadOnlyList<DeliveryGap> DeliveryGaps)
{
    public HeldSpan? LongestHeld => HeldSpans.Count == 0 ? null : HeldSpans.MaxBy(span => span.Seconds);
    public DeliveryGap? LongestDeliveryGap => DeliveryGaps.Count == 0 ? null : DeliveryGaps.MaxBy(gap => gap.Seconds);
}

/// <summary>
/// v0.4.8 hotfix（UI Automation 負荷）: 出力トレースから「映像の中身が更新され続けたか」を数える。
///
/// **Spout の送信が続いていることと、映像の中身が更新されていることは別**なので、送信回数ではなく
/// 合成が新しいソースフレームを採ったかで見る。合成の各 tick（compose.acquire）は、
/// 取得できたフレーム番号（imageId）が前回と同じか、Ready でなければ「前の絵を描いた（Held）」。
/// Held が続いた区間ごとに、同じ時間帯に shim の配信（gst.delivery）が何件あったかを数えて、
/// 止まっていたのが**再生側か合成側か**を分ける。
/// </summary>
internal static class OutputContinuityAudit
{
    /// <summary>配信から合成が採るまでに許す遅れ（60Hz の 2 tick 分）。</summary>
    private const double LateTakeAllowanceSeconds = 0.034;

    public static IEnumerable<TraceEvent> ReadEvents(string eventsJsonlPath)
    {
        foreach (string line in File.ReadLines(eventsJsonlPath))
        {
            if (line.Length == 0) continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string stage = root.GetProperty("stage").GetString() ?? "";
            if (stage is not ("compose.acquire" or "gst.delivery")) continue;
            yield return new TraceEvent(
                stage,
                root.GetProperty("qpc").GetInt64(),
                root.GetProperty("imageId").GetInt64(),
                root.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString()
                    : null);
        }
    }

    /// <param name="events">qpc の昇順でなくてよい（中で並べ替える）。</param>
    /// <param name="frequency">qpc の周波数（Stopwatch.Frequency）。</param>
    /// <param name="minHeldSeconds">この長さ以上続いた Held だけを区間として返す。</param>
    /// <param name="minDeliveryGapSeconds">この長さ以上空いた配信の間隔だけを返す。</param>
    /// <param name="fromQpc">これより前の事象は数えない（起動・プロジェクト読み込みの一時停止区間を除く）。</param>
    public static OutputContinuitySummary Summarize(IEnumerable<TraceEvent> events, long frequency,
        double minHeldSeconds = 0.25, double minDeliveryGapSeconds = 0.1, long fromQpc = long.MinValue)
    {
        var ordered = events.Where(e => e.Qpc >= fromQpc).OrderBy(e => e.Qpc).ToList();
        var deliveries = ordered.Where(e => e.Stage == "gst.delivery").Select(e => e.Qpc).ToList();

        var heldSpans = new List<HeldSpan>();
        long lastDrawnImage = -1;
        long? heldStart = null;
        long heldEnd = 0;
        int heldTicks = 0;
        int composeTicks = 0;
        foreach (TraceEvent e in ordered.Where(e => e.Stage == "compose.acquire"))
        {
            composeTicks++;
            bool drewNew = string.Equals(e.Detail, "Ready", StringComparison.Ordinal)
                           && e.ImageId > 0 && e.ImageId != lastDrawnImage;
            if (drewNew)
            {
                lastDrawnImage = e.ImageId;
                Close(e.Qpc);
                continue;
            }
            heldStart ??= e.Qpc;
            heldEnd = e.Qpc;
            heldTicks++;
        }
        Close(heldEnd);

        var gaps = new List<DeliveryGap>();
        for (int i = 1; i < deliveries.Count; i++)
        {
            double seconds = (deliveries[i] - deliveries[i - 1]) / (double)frequency;
            if (seconds >= minDeliveryGapSeconds)
                gaps.Add(new DeliveryGap(deliveries[i - 1], deliveries[i], seconds));
        }
        return new OutputContinuitySummary(composeTicks, deliveries.Count, heldSpans, gaps);

        void Close(long endQpc)
        {
            if (heldStart is not long start) return;
            // 区間の終わりは「次に新しい絵を描いた tick」。Held の最後の tick ではなく、そこまでが同じ絵。
            double seconds = (endQpc - start) / (double)frequency;
            if (seconds >= minHeldSeconds)
            {
                // 区間を終わらせた配信（次の tick で採られる）と、届いてから 2 tick 以内の配信（フェンス待ちなど
                // 通常の遅れ）は数えない。最後の Held tick より十分前に届いていたのに採らなかった分だけを数える。
                long takenLatest = heldEnd - (long)(LateTakeAllowanceSeconds * frequency);
                int during = CountBetween(deliveries, start, takenLatest);
                heldSpans.Add(new HeldSpan(start, endQpc, seconds, heldTicks, during));
            }
            heldStart = null;
            heldTicks = 0;
        }
    }

    private static int CountBetween(List<long> sortedQpcs, long start, long end)
    {
        int count = 0;
        foreach (long qpc in sortedQpcs)
        {
            if (qpc > end) break;
            if (qpc >= start) count++;
        }
        return count;
    }

    public static string Describe(HeldSpan span, long originQpc, long frequency) =>
        string.Create(CultureInfo.InvariantCulture,
            $"+{(span.StartQpc - originQpc) / (double)frequency:F3}s {span.Seconds * 1000:F0}ms ticks={span.Ticks} deliveries={span.DeliveriesDuring}");
}
