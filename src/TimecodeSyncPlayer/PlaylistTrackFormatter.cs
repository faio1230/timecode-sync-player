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
    /// hh:mm:ss:ff 形式の文字列を TimeSpan にパースする。区切り文字は `:` と `;` の
    /// どちらでもよい（`;` は現場の慣習でドロップフレーム表記に使われるが、このアプリは
    /// 番号付けを扱わないため入力としては同じ値として解釈する）。
    /// 範囲外の値は拒否せず、ff は [0, fps-1] へ丸め、ss/mm は 60 単位で上の桁へ繰り上げる
    /// （hours は繰り上げ後を含めて 0〜99。超える場合は false）。
    /// 丸め・繰り上げが起きた場合は adjusted を true にする（呼び出し側の UI 書き戻し用）。
    /// この関数はタイムライン編集専用で、ドロップフレームの番号付けや 29.97 の実時間換算
    /// （LtcTimecode.ToRealSeconds）は扱わない。
    /// </summary>
    public static bool TryParseTimecode(string text, int fps, out TimeSpan result, out bool adjusted)
    {
        result = TimeSpan.Zero;
        adjusted = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > 50) return false;
        if (fps <= 0) return false;

        string[] parts = text.Split(':', ';');
        if (parts.Length != 4) return false;

        if (!int.TryParse(parts[0], out int hours)) return false;
        if (!int.TryParse(parts[1], out int minutes)) return false;
        if (!int.TryParse(parts[2], out int seconds)) return false;
        if (!int.TryParse(parts[3], out int frames)) return false;

        if (hours < 0 || minutes < 0 || seconds < 0) return false;

        bool neededAdjustment = false;
        if (frames < 0)
        {
            frames = 0;
            neededAdjustment = true;
        }
        else if (frames >= fps)
        {
            frames = fps - 1;
            neededAdjustment = true;
        }

        if (seconds >= 60 || minutes >= 60)
            neededAdjustment = true;

        /* 秒数の総和で繰り上げを表す（long にして桁溢れを避ける）。
         * ff の丸め込みは秒へ繰り上がらない（fps-1 は 1 秒未満）。 */
        long totalSeconds = (long)hours * 3600 + (long)minutes * 60 + seconds;
        if (totalSeconds >= 100L * 3600) return false;

        result = TimeSpan.FromSeconds(totalSeconds + (double)frames / fps);
        adjusted = neededAdjustment;
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
