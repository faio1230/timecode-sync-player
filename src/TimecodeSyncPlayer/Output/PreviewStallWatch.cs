namespace TimecodeSyncPlayer.Output;

/// <summary>
/// v0.5.1: 画面のプレビュー（VideoImage）が更新されない時間を見張る（UI スレッド）。
/// 検証機で、合成と Spout は黒を出しているのに、プレビューだけが 5 秒間黒にならなかった
/// （ギャップ入り。RTX で 1 回、内蔵 GPU で 2 回）。どこで止まっているかをログで切り分けるための計測。
/// 最初のプレビューが出るまでは見張らない（起動直後・再生不可の状態を止まりと数えない）。
/// </summary>
internal sealed class PreviewStallWatch
{
    private readonly long _thresholdTicks;
    private long _lastShownTicks;
    private long _stallStartedTicks;
    private bool _stalled;

    public PreviewStallWatch(long thresholdTicks)
    {
        _thresholdTicks = thresholdTicks;
    }

    /// <summary>プレビューを画面へ書き込んだ。止まっていたなら、その長さ（ticks）を返す。</summary>
    public long? Shown(long nowTicks)
    {
        long? ended = _stalled ? nowTicks - _lastShownTicks : null;
        _stalled = false;
        _lastShownTicks = nowTicks;
        return ended;
    }

    /// <summary>定期に呼ぶ。止まりが始まった 1 回だけ、最後に書き込んでからの ticks を返す。</summary>
    public long? Check(long nowTicks)
    {
        if (_lastShownTicks == 0 || _stalled) return null;
        long age = nowTicks - _lastShownTicks;
        if (age < _thresholdTicks) return null;
        _stalled = true;
        _stallStartedTicks = nowTicks;
        return age;
    }

    public bool IsStalled => _stalled;
    public long StallStartedTicks => _stallStartedTicks;
}
