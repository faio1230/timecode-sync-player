namespace TimecodeSyncPlayer.Output;

/// <summary>合成層が解釈するギャップ状態。既存 GapRenderFrameDecision の写像。</summary>
internal enum OutputGapMode { None, Black, Hold, GapFreeze }

/// <summary>
/// UI が mailbox で GPU worker へ渡すタイムライン状態（不変レコード）。
/// 出力側はこの状態とソース画像だけを使い、クリップやシークの判断を持たない。
/// </summary>
internal sealed record TimelineOutputState(
    int Generation,
    OutputGapMode Gap,
    bool TestCardEnabled,
    CanvasSettings Canvas,
    ClipPlacement Clip,
    double PositionSeconds,
    double? FreezeTargetSeconds = null)
{
    public static readonly TimelineOutputState Default = new(
        0, OutputGapMode.None, false, CanvasSettings.Default, new ClipPlacement(null), 0);

    /// <summary>
    /// K3 f4-14: 合成層が Freeze の保存で取得画像の位置と比べる値。Freeze の目標（UI の確定の門と
    /// 同じ目標）があればそれ、無ければ照会位置。照会位置はシーク直後のポンプ中にパイプライン値
    /// （尺 + 2 フレーム）へ固定されることがあり、目標フレームでも窓を外れていた。
    /// </summary>
    public double FreezeComparisonSeconds => FreezeTargetSeconds ?? PositionSeconds;

    /// <summary>現在クリップの配置。トラックの Fit（null はプロジェクト既定の継承）を写す。</summary>
    public static ClipPlacement PlacementFor(PlaylistTrack? track) => new(track?.Fit);
}

/// <summary>最新1件だけを保持する TimelineOutputState の mailbox。UI が書き、GPU worker が読む。</summary>
internal sealed class TimelineOutputMailbox
{
    private readonly object gate = new();
    private TimelineOutputState? latest;

    public void Publish(TimelineOutputState state)
    {
        lock (gate) latest = state;
    }

    public TimelineOutputState? Take()
    {
        lock (gate)
        {
            var value = latest;
            latest = null;
            return value;
        }
    }
}
