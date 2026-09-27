namespace TimecodeSyncPlayer;

internal enum GapFrameCaptureDecision
{
    None,
    RenderAndCapture
}

internal static class GapFrameCaptureCoordinator
{
    public static GapFrameCaptureDecision Decide(
        GapState state,
        bool hasFrame,
        bool isExpectedPath,
        bool hasTimePosition,
        double actualPositionSeconds,
        double targetSeconds,
        double fps,
        bool isNativeSeeking = false,
        bool allowRedraw = false,
        bool frameSeenSinceCapture = true,
        double? deliveredFramePositionSeconds = null)
    {
        if ((!hasFrame && !allowRedraw) || !isExpectedPath || isNativeSeeking)
            return GapFrameCaptureDecision.None;

        double effectiveFps = fps > 0 ? fps : 30.0;
        double frameSeconds = 1.0 / effectiveFps;

        if (state is GapState.EnteringFreeze or GapState.WaitingForFrameStep)
        {
            // D21: 進入・再ロードの後に届いたフレームだけを最終フレームとして固定する。
            if (!frameSeenSinceCapture)
                return GapFrameCaptureDecision.None;

            // C-4: 位置の窓は、照会の値ではなく D21-b で受け入れた配信フレームの PTS で見る。
            // シーク直後の照会は、ポンプ中にパイプライン値（尺 + 2 フレーム）へ落ちることがある。
            double windowPositionSeconds = deliveredFramePositionSeconds is double delivered &&
                double.IsFinite(delivered)
                ? delivered
                : actualPositionSeconds;

            return ContinueModePlaybackPolicy.ShouldCaptureFreezeFrameAfterFrameStep(
                hasTimePosition,
                windowPositionSeconds,
                targetSeconds,
                frameSeconds)
                ? GapFrameCaptureDecision.RenderAndCapture
                : GapFrameCaptureDecision.None;
        }

        return GapFrameCaptureDecision.None;
    }
}

internal static class GapCaptureHandoffLog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HandoffState> Sites = new();
    private static volatile bool enabled;

    public static bool Enabled
    {
        get => enabled;
        set
        {
            lock (Gate)
            {
                if (value && !enabled)
                    Sites.Clear();
                enabled = value;
            }
        }
    }

    public static void Record(string site, string reason, string fields)
    {
        if (!enabled)
            return;
        string key = reason + "|" + fields;
        long previousRepeats;
        lock (Gate)
        {
            if (!Sites.TryGetValue(site, out HandoffState? state))
            {
                state = new HandoffState();
                Sites.Add(site, state);
            }
            if (string.Equals(state.Key, key, StringComparison.Ordinal))
            {
                state.Repeats++;
                return;
            }
            previousRepeats = state.Repeats;
            state.Key = key;
            state.Repeats = 0;
        }
        Serilog.Log.Debug("Gap capture handoff: {Site} {Reason} {Fields} previousRepeats={PreviousRepeats}",
            site, reason, fields, previousRepeats);
    }

    private sealed class HandoffState
    {
        public string Key { get; set; } = string.Empty;
        public long Repeats { get; set; }
    }
}
