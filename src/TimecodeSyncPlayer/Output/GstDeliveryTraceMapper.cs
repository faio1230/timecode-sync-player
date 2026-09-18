using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// shim の配信リングイベント（TcsDeliveryEvent）を events.jsonl の段階イベントへ写す純関数。
/// 通常のフレーム到着は gst.delivery のまま。flags bit 3 (8) は
/// tcs_player_get_time_pos が同時点で記録した position スナップショットで、gst.position
/// として区別する: imageId = 最新 delivery の seq、ptsNs = 最新 delivery の PTS、
/// value = クエリで得た position（µs、gst.position のみ）。
/// 0.4.5-A: bit 4 (16) が付いたスナップショットはクエリ失敗のフォールバックで、
/// gst.positionFallback として区別する（value = 返した配信 PTS、µs）。
/// 0.4.5-A フェーズ 2: bit 5 (32) は旧世代の PTS を返さず失敗したフォールバックで、
/// gst.positionFallbackRejected として区別する（value = 0、弾いた回数を数えられる）。
/// </summary>
internal static class GstDeliveryTraceMapper
{
    /// <summary>tcs_gstreamer.h の flags bit 3（position スナップショット）。</summary>
    internal const uint PositionFlag = 8;

    /// <summary>tcs_gstreamer.h の flags bit 4（クエリ失敗のフォールバック）。</summary>
    internal const uint PositionFallbackFlag = 16;

    /// <summary>tcs_gstreamer.h の flags bit 5（旧世代のためフォールバックを破棄）。</summary>
    internal const uint PositionFallbackRejectedFlag = 32;

    internal static OutputTraceEvent Map(GstNative.TcsDeliveryEvent e) =>
        (e.Flags & PositionFlag) != 0
            ? (e.Flags & PositionFallbackRejectedFlag) != 0
                ? new OutputTraceEvent("gst.positionFallbackRejected", "GST", (long)e.Qpc, ImageId: (long)e.Seq,
                    Value: e.RunningNs / 1000, PtsNs: (long)e.PtsNs)
                : (e.Flags & PositionFallbackFlag) != 0
                    ? new OutputTraceEvent("gst.positionFallback", "GST", (long)e.Qpc, ImageId: (long)e.Seq,
                        Value: e.RunningNs / 1000, PtsNs: (long)e.PtsNs)
                    : new OutputTraceEvent("gst.position", "GST", (long)e.Qpc, ImageId: (long)e.Seq,
                        Value: e.RunningNs / 1000, PtsNs: (long)e.PtsNs)
            : new OutputTraceEvent("gst.delivery", "GST", (long)e.Qpc, ImageId: (long)e.Seq,
                Detail: $"{e.PtsNs}:{e.RunningNs}:{e.Flags}", Value: e.CallbackUs);
}
