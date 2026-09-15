using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// shim の配信リングイベント（TcsDeliveryEvent）を events.jsonl の段階イベントへ写す純関数。
/// 通常のフレーム到着は gst.delivery のまま。flags bit 3 (8) は
/// tcs_player_get_time_pos が同時点で記録した position スナップショットで、gst.position
/// として区別する: imageId = 最新 delivery の seq、ptsNs = 最新 delivery の PTS、
/// value = クエリで得た position（µs、gst.position のみ）。
/// </summary>
internal static class GstDeliveryTraceMapper
{
    /// <summary>tcs_gstreamer.h の flags bit 3（position スナップショット）。</summary>
    internal const uint PositionFlag = 8;

    internal static OutputTraceEvent Map(GstNative.TcsDeliveryEvent e) =>
        (e.Flags & PositionFlag) != 0
            ? new OutputTraceEvent("gst.position", "GST", (long)e.Qpc, ImageId: (long)e.Seq,
                Value: e.RunningNs / 1000, PtsNs: (long)e.PtsNs)
            : new OutputTraceEvent("gst.delivery", "GST", (long)e.Qpc, ImageId: (long)e.Seq,
                Detail: $"{e.PtsNs}:{e.RunningNs}:{e.Flags}", Value: e.CallbackUs);
}
