namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.2 段 2e: 読み込みの状態（位置の持ち主の軸のうち、ロード中と解除の回収待ち）。
/// 段階は「なし／ロード中（開始時刻・開始位置・開始時の描画枚数）／解除の回収待ち（解除時刻）」。
/// ロード解除の副作用（着地窓・デバウンス）とログは <see cref="TimecodeSyncService"/> に残し、
/// ここは値と遷移だけを持つ。
///
/// <see cref="IsLoadingFile"/> は今まで volatile bool として読まれていた。リポジトリ内の
/// 呼び出し元はすべて UI スレッド（LTC フレームは MainWindow が Dispatcher 経由で渡す）だが、
/// public な <see cref="TimecodeSyncService"/> の外からも読めるため、volatile の鏡を段階と
/// 一緒に更新して残す。段階のデータ（<see cref="Loading"/>）は UI スレッドだけが読む。
/// </summary>
internal sealed class FileLoadState
{
    /// <summary>ロード中だけ意味を持つデータ。</summary>
    internal readonly record struct Loading(
        DateTime StartedAt, double StartPositionSeconds, long StartedRenderedFrames);

    private Loading? _loading;
    private DateTime _releasedAt = DateTime.MinValue;
    private bool _releasePending;
    // IsLoadingFile の鏡。段階の型を別スレッドから読ませないため、bool だけを volatile で持つ。
    private volatile bool _isLoadingFile;

    /// <summary>ロード中か。</summary>
    public bool IsLoadingFile => _isLoadingFile;

    /// <summary>D27-b: 解除の回収待ちか。</summary>
    public bool HasPendingRelease => _releasePending;

    /// <summary>ロードの開始時刻（ロード中だけ意味を持つ。段階の外では初期値）。</summary>
    public DateTime StartedAt => _loading?.StartedAt ?? DateTime.MinValue;

    /// <summary>ロードの開始位置（ロード中だけ意味を持つ。段階の外では初期値）。</summary>
    public double StartPositionSeconds => _loading?.StartPositionSeconds ?? 0.0;

    /// <summary>ロード開始時の描画枚数（ロード中だけ意味を持つ。段階の外では初期値）。</summary>
    public long StartedRenderedFrames => _loading?.StartedRenderedFrames ?? 0;

    /// <summary>解除時刻（回収待ちのときだけ意味を持つ）。</summary>
    public DateTime ReleasedAt => _releasedAt;

    /// <summary>ロードを開始した（回収待ちは呼び出し側が <see cref="ClearReleasePending"/> で下ろす）。</summary>
    public void Begin(DateTime startedAt, double startPositionSeconds, long startedRenderedFrames)
    {
        _isLoadingFile = true;
        _loading = new Loading(startedAt, startPositionSeconds, startedRenderedFrames);
    }

    /// <summary>ロードを解除した（ロード中から回収待ちへ移る）。</summary>
    public void Release(DateTime now)
    {
        _isLoadingFile = false;
        _loading = null;
        _releasePending = true;
        _releasedAt = now;
    }

    /// <summary>安全タイムアウトでロード中をやめた（回収待ちにはしない）。</summary>
    public void MarkTimedOut()
    {
        _isLoadingFile = false;
        _loading = null;
    }

    /// <summary>
    /// v0.5.3 段 3d: ロード中と解除の回収待ちを両方下ろす（§6 の 3）。解除ではない
    /// （<see cref="Release"/> を通らない。解除時刻を残さず、着地窓・デバウンスにも触れない）。
    /// </summary>
    public void Cancel()
    {
        _isLoadingFile = false;
        _loading = null;
        _releasePending = false;
    }

    /// <summary>解除を回収した（回収待ちを下ろす）。</summary>
    public void CollectRelease() => _releasePending = false;

    /// <summary>回収待ちを下ろす（新しいロードの開始時に古い解除を捨てる）。</summary>
    public void ClearReleasePending() => _releasePending = false;
}
