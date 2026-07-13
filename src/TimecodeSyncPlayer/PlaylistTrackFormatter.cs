namespace TimecodeSyncPlayer;

/// <summary>
/// PlaylistTrack の UI 表示用フォーマットを担当する。
/// ドメインモデルから UI 固有のフォーマットロジックを分離する。
/// </summary>
public static class PlaylistTrackFormatter
{
    /// <summary>
    /// TimelineIn を hh:mm:ss:ff 形式で取得する。
    /// </summary>
    public static string FormatTimelineIn(PlaylistTrack track)
    {
        int fps = GetFps(track);
        return FormatTimecode(track.GetActualTimelineIn(), fps);
    }

    /// <summary>
    /// TimelineOffset を hh:mm:ss:ff 形式で取得する。
    /// </summary>
    public static string FormatTimelineOffset(PlaylistTrack track)
    {
        int fps = GetFps(track);
        return FormatTimecode(track.TimelineOffset, fps);
    }

    /// <summary>
    /// 動画の総再生時間を hh:mm:ss:ff 形式で取得する。
    /// </summary>
    public static string FormatMediaDuration(PlaylistTrack track)
    {
        int fps = GetFps(track);
        return FormatTimecode(track.MediaDuration, fps);
    }

    /// <summary>
    /// 実効再生時間を hh:mm:ss:ff 形式で取得する。
    /// </summary>
    public static string FormatEffectiveDuration(PlaylistTrack track)
    {
        int fps = GetFps(track);
        return FormatTimecode(track.GetEffectiveDuration(), fps);
    }

    /// <summary>
    /// Timeline 範囲を表示用の文字列で取得する。
    /// </summary>
    public static string FormatTimelineRange(PlaylistTrack track)
    {
        var actualIn = track.GetActualTimelineIn();
        var actualOut = track.GetActualTimelineOut();
        int fps = GetFps(track);
        return $"{FormatTimecode(actualIn, fps)} → {FormatTimecode(actualOut, fps)}";
    }

    /// <summary>
    /// hh:mm:ss:ff 形式の文字列を TimeSpan にパースする。
    /// </summary>
    public static bool TryParseTimecode(string text, int fps, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > 50) return false;

        string[] parts = text.Split(':');
        if (parts.Length != 4) return false;

        if (!int.TryParse(parts[0], out int hours)) return false;
        if (!int.TryParse(parts[1], out int minutes)) return false;
        if (!int.TryParse(parts[2], out int seconds)) return false;
        if (!int.TryParse(parts[3], out int frames)) return false;

        if (frames < 0 || frames >= fps) return false;
        if (seconds < 0 || seconds >= 60) return false;
        if (minutes < 0 || minutes >= 60) return false;
        if (hours < 0 || hours > 99) return false;

        double totalSeconds = hours * 3600.0 + minutes * 60.0 + seconds + (double)frames / fps;
        result = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    public static string FormatTimecode(TimeSpan ts, int fps)
    {
        if (fps <= 0) fps = 30;

        int totalFrames = (int)Math.Round(ts.TotalSeconds * fps);
        int frames = totalFrames % fps;
        int totalSeconds = (int)(totalFrames / fps);
        int seconds = totalSeconds % 60;
        int minutes = (totalSeconds / 60) % 60;
        int hours = totalSeconds / 3600;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}:{frames:D2}";
    }

    private static int GetFps(PlaylistTrack track)
    {
        return track.FrameRate > 0 ? (int)Math.Round(track.FrameRate.Value) : 30;
    }
}
