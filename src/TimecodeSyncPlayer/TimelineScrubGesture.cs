namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 F-2: タイムラインのドラッグ（スクラブ）の行の扱い。UI に依存しない純粋な部品。
/// 押したときの行（track）に固定してドラッグし、ドラッグ中に行をまたいでも別のファイルを読み込まない
/// （目標の計算は押した行で行う）。離した位置が別の行の上なら、今のクリック（MouseLeftButtonUp）と
/// 同じ振る舞い（離した行の番号でクリックのシークを要求する）にする。
/// 動かさずに離したとき（ただのクリック）は、今のクリックのまま（ドラッグは始まらない）。
/// </summary>
internal sealed class TimelineScrubGesture
{
    private bool _pressed;
    private int? _pinnedTrackIndex;
    private double _pressX;

    /// <summary>押した行に固定してドラッグしている。</summary>
    public bool IsDragging { get; private set; }

    /// <summary>押したときの行（行の外で押したときは null）。</summary>
    public int? PinnedTrackIndex => _pinnedTrackIndex;

    /// <summary>押した。行の外（null）で押したときはドラッグを始めない。</summary>
    public void Press(int? trackIndex, double x)
    {
        _pressed = trackIndex.HasValue;
        _pinnedTrackIndex = trackIndex;
        _pressX = x;
        IsDragging = false;
    }

    /// <summary>
    /// 押したまま動かした。ドラッグの距離（<paramref name="dragThreshold"/>）を超えたらドラッグを始める。
    /// 行は押したときの行のまま（縦の位置は見ない）。
    /// </summary>
    public TimelineScrubMove Move(double x, double dragThreshold)
    {
        if (!_pressed || _pinnedTrackIndex is not int pinned)
            return TimelineScrubMove.None;
        bool started = false;
        if (!IsDragging)
        {
            if (Math.Abs(x - _pressX) < Math.Max(0, dragThreshold))
                return TimelineScrubMove.None;
            IsDragging = true;
            started = true;
        }
        return new TimelineScrubMove(true, started, pinned);
    }

    /// <summary>
    /// 離した。<paramref name="releaseTrackIndex"/> は離した位置の行（行の外は null）。
    /// </summary>
    public TimelineScrubRelease Release(int? releaseTrackIndex)
    {
        bool wasDragging = IsDragging;
        int? pinned = _pinnedTrackIndex;
        _pressed = false;
        _pinnedTrackIndex = null;
        IsDragging = false;

        if (!wasDragging || pinned is not int pinnedIndex)
        {
            // ただのクリック: 今のまま（離した行でシーク、行の外なら何もしない）。
            return new TimelineScrubRelease(false, releaseTrackIndex, UsesPinnedTrack: false);
        }
        if (releaseTrackIndex is int other && other != pinnedIndex)
        {
            // 別の行の上で離した: 今のクリックと同じ振る舞い（離した行の番号）。
            return new TimelineScrubRelease(true, other, UsesPinnedTrack: false);
        }
        // 同じ行か行の外で離した: 押した行のまま締める。
        return new TimelineScrubRelease(true, pinnedIndex, UsesPinnedTrack: true);
    }

    /// <summary>捕捉を失ったときなどに状態を捨てる。</summary>
    public void Cancel()
    {
        _pressed = false;
        _pinnedTrackIndex = null;
        IsDragging = false;
    }
}

/// <summary>ドラッグの 1 回の動き。<see cref="Moved"/> が false なら何もしない。</summary>
internal readonly record struct TimelineScrubMove(bool Moved, bool Started, int TrackIndex)
{
    public static TimelineScrubMove None => new(false, false, -1);
}

/// <summary>
/// 離したときの扱い。<see cref="EndsDrag"/> が true ならドラッグを締める（スクラブの最後の 1 本）。
/// <see cref="TrackIndex"/> はシークを要求する行（null なら要求しない）。
/// </summary>
internal readonly record struct TimelineScrubRelease(bool EndsDrag, int? TrackIndex, bool UsesPinnedTrack);
