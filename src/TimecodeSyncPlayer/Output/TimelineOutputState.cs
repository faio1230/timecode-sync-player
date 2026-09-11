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
    double PositionSeconds)
{
    public static readonly TimelineOutputState Default = new(
        0, OutputGapMode.None, false, CanvasSettings.Default, new ClipPlacement(null), 0);
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
