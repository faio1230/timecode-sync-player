namespace TimecodeSyncPlayer;

public sealed record PlaylistTrack(
    Guid Id,
    string FilePath,
    string Name,
    TimeSpan MediaIn,
    TimeSpan? MediaOut,
    TimeSpan TimelineOffset,
    TimeSpan MediaDuration,
    TimeSpan SyncOffset,
    double? FrameRate,
    bool IsEnabled)
{
    /// <summary>
    /// トラックの実効再生時間を取得する。
    /// </summary>
    public TimeSpan GetEffectiveDuration()
    {
        TimeSpan outPoint = MediaOut ?? MediaDuration;
        TimeSpan duration = outPoint - MediaIn;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>
    /// Timeline上の実際の開始位置を返す（TimelineOffset）。
    /// </summary>
    public TimeSpan GetActualTimelineIn()
    {
        return TimelineOffset;
    }

    /// <summary>
    /// Timeline上の実際の終了位置を計算する。
    /// </summary>
    public TimeSpan GetActualTimelineOut()
    {
        return TimelineOffset + GetEffectiveDuration();
    }

    /// <summary>
    /// UIバインディング用: TimelineInをhh:mm:ss:ff形式で取得する。
    /// </summary>
    public string TimelineInText => PlaylistTrackFormatter.FormatTimelineIn(this);

    /// <summary>
    /// UIバインディング用: TimelineOffsetをhh:mm:ss:ff形式で取得する。
    /// </summary>
    public string TimelineOffsetText => PlaylistTrackFormatter.FormatTimelineOffset(this);

    /// <summary>
    /// UIバインディング用: 動画の総再生時間をhh:mm:ss:ff形式で取得する。
    /// </summary>
    public string MediaDurationText => PlaylistTrackFormatter.FormatMediaDuration(this);

    /// <summary>
    /// UIバインディング用: 実効再生時間をhh:mm:ss:ff形式で取得する。
    /// </summary>
    public string EffectiveDurationText => PlaylistTrackFormatter.FormatEffectiveDuration(this);
}
