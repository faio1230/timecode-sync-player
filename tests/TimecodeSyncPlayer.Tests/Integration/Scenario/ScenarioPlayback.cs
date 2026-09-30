using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C2: 時間で動く偽の再生 API（設計: docs/design/v0.5.4-scenario-layer.md §2-2）。
/// ScenarioClock の <see cref="ScenarioClock.Advanced"/> から <see cref="AdvanceTime"/> を呼ぶと、
/// 再生位置・シークの着地・ロード・尺の到着が仮想時間で進む。設定は既定ですべて 0（即時）で、
/// harness の旧フィールドと同じ振る舞いになる。
/// </summary>
internal sealed class ScenarioPlayback : IPlaybackApi
{
    private double _positionSeconds;
    private double _durationSeconds;
    private bool _durationKnown;
    private double _fps;
    private bool _fpsKnown;
    private string _path = "";
    private string _videoCodec = "";
    private bool _isPaused = true;
    private double _rate = 1.0;
    private bool _hasMedia;

    private long _nowMilliseconds;
    private long _loadReadyAtMilliseconds = -1;
    private double _loadStartSeconds;
    private bool _loadPaused;
    private long _seekLandingAtMilliseconds = -1;
    private double _seekTargetSeconds;
    private bool _settleToTargetPending;
    private long _durationReadyAtMilliseconds = -1;
    private ulong _generation;
    private double _deliveredSeconds;
    private ulong _deliveredGeneration;

    public ScenarioPlayback(
        double positionSeconds = 0,
        double durationSeconds = 0,
        double fps = 0)
    {
        _positionSeconds = positionSeconds;
        _durationSeconds = durationSeconds;
        _durationKnown = durationSeconds > 0;
        _fps = fps;
        _fpsKnown = fps > 0;
    }

    // ---- 設定（既定はすべて 0 = 即時。harness の旧フィールドと同じ） ----

    public double SeekLandingDelaySeconds { get; set; }
    public double SeekOvershootSeconds { get; set; }
    public double LoadDurationSeconds { get; set; }
    public double DurationArrivalDelaySeconds { get; set; }
    public bool SeekSucceeds { get; set; } = true;
    public bool LoadSucceeds { get; set; } = true;
    public bool RateApplySucceeds { get; set; } = true;

    /// <summary>D37-b の「位置が後退した」状態を作る（真の間は時間で戻る）。</summary>
    public bool PositionGoesBackward { get; set; }

    // ---- v0.5.4 K3: EOS（A1 型の再現用）。ここだけ既定 false で、既存の意味は変えない ----

    /// <summary>
    /// パイプラインが EOS を観測した（以降、新しいフレームは来ない）。
    /// shim と同じ契約で、次の Seek / Load が成立すると解除する
    /// （`seek_prepare_locked` が `p->eos` を消す。native/gst-shim/src/tcs_gstreamer.cpp:2819-2823）。
    /// </summary>
    public bool Ended { get; private set; }

    /// <summary>
    /// EOS 中の位置クエリが返す値。既定は「尺 + 1 フレーム」（検証機の観測:
    /// 尺 20.000 / 30fps で 20.033）。これは shim の `get_time_pos` が、現世代の配信フレームが
    /// 無いとき pipeline の位置へ落ちる値（tcs_gstreamer.cpp:3765-3810）の模擬。
    /// </summary>
    public double? EndedPositionSeconds { get; set; }

    /// <summary>EOS を観測したとして記録する。解除は次の Seek / Load。</summary>
    public void MarkEnded()
    {
        Ended = true;
        if (!EndedPositionSeconds.HasValue)
            EndedPositionSeconds = _durationSeconds > 0
                ? _durationSeconds + (_fpsKnown && _fps > 0 ? 1.0 / _fps : 0)
                : _positionSeconds;
    }

    // ---- 状態 ----

    public double PositionSeconds => _positionSeconds;
    public double DurationSeconds => _durationSeconds;
    public double Fps => _fps;
    public double Rate => _rate;
    public bool Paused => _isPaused;
    public bool HasMedia => _hasMedia;
    public bool IsLoading => _loadReadyAtMilliseconds >= 0;
    public bool HasPendingSeek => _seekLandingAtMilliseconds >= 0;
    public int Width { get; set; }
    public int Height { get; set; }
    public string VideoCodec { get => _videoCodec; set => _videoCodec = value; }

    // ---- v0.5.4 段 B の準備: 着地の事象（世代の最初のフレームの配信） ----

    /// <summary>いまのプレイヤー世代（シーク・ロードで進む。shim の `p->generation` と同じ）。</summary>
    public ulong CurrentGeneration => _generation;

    /// <summary>最後に配信されたフレームの位置（無ければ 0）。shim の `latest_pts_ns` の模擬。</summary>
    public double DeliveredSeconds => _deliveredSeconds;

    /// <summary>
    /// 最後に配信されたフレームの世代（無ければ 0）。着地待ちの間はシーク前の値のまま。
    /// shim の `latest_gen`（tcs_gstreamer.cpp:1568）と同じ意味。
    /// </summary>
    public ulong DeliveredGeneration => _deliveredGeneration;

    /// <summary>着地の事象（世代の最初のフレームの配信）の回数。連続シークでは最後の世代だけが 1 回。</summary>
    public int LandingCount { get; private set; }

    /// <summary>着地の事象の口。シーク・ロードの世代の最初のフレームを配信したときに、その世代を渡す。</summary>
    public event Action<ulong>? Landed;

    /// <summary>テストが位置を直接与える（旧 harness の AdvancePlayback と同じ）。</summary>
    public void SetPosition(double seconds)
    {
        _positionSeconds = seconds;
        _generation++;
        DeliverFrame();
    }

    /// <summary>
    /// v0.5.4 B4b のテスト用: 照会位置だけを差し替え、配信したフレーム（PTS・世代）は前のまま
    /// 残す（実機の「照会した位置が先行し、配信したフレームは遅れている」状態）。
    /// </summary>
    public void SetPositionWithoutDelivery(double seconds) => _positionSeconds = seconds;

    /// <summary>v0.5.4 段 B3 のテスト用: 読み込みの世代を進め、最初のフレームはまだ配信しない。</summary>
    public void BeginLoadWithoutDelivery() => _generation++;

    /// <summary>v0.5.4 段 B3 のテスト用: 読み込みの世代の最初のフレームを配信する（着地の事象）。</summary>
    public void DeliverLoadLanding() => DeliverLanding();

    public void SetDuration(double seconds)
    {
        _durationSeconds = seconds;
        if (_durationReadyAtMilliseconds < 0)
            _durationKnown = true;
    }

    public void SetFps(double fps)
    {
        _fps = fps;
        _fpsKnown = fps > 0;
    }

    public void SetMedia(double durationSeconds, double fps)
    {
        SetDuration(durationSeconds);
        SetFps(fps);
    }

    /// <summary>仮想時間を進め、予約されたロード・着地・尺の到着と再生の進みを処理する。</summary>
    public void AdvanceTime(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "ScenarioPlayback は戻らない");

        _nowMilliseconds += (long)delta.TotalMilliseconds;

        bool landedThisStep = ApplyPendingSeek();
        bool loadedThisStep = ApplyPendingLoad();
        ApplyDurationArrival();

        if (landedThisStep || loadedThisStep)
            return;

        if (_settleToTargetPending)
        {
            _positionSeconds = _seekTargetSeconds;
            _settleToTargetPending = false;
        }

        if (!_hasMedia || _isPaused || IsLoading || HasPendingSeek)
            return;

        double direction = PositionGoesBackward ? -1.0 : 1.0;
        _positionSeconds += delta.TotalSeconds * _rate * direction;
        _generation++;
        DeliverFrame();
    }

    // ---- IPlaybackApi ----

    public PlaybackResult Load(string path, double? startSeconds, bool paused)
    {
        if (!LoadSucceeds)
            return PlaybackResult.Fail("scenario: load rejected");

        Ended = false;    // shim: ロードは新しいパイプラインで EOS 状態を持たない
        _path = path;
        _hasMedia = true;
        _generation++;
        double start = Math.Max(0.0, startSeconds ?? 0.0);

        if (LoadDurationSeconds <= 0)
        {
            _positionSeconds = start;
            _isPaused = paused;
            DeliverLanding();
        }
        else
        {
            _isPaused = true;
            _loadReadyAtMilliseconds = _nowMilliseconds + Milliseconds(LoadDurationSeconds);
            _loadStartSeconds = start;
            _loadPaused = paused;
        }

        ScheduleDurationArrival();
        return PlaybackResult.Ok;
    }

    public PlaybackResult Seek(double seconds)
    {
        if (!SeekSucceeds)
            return PlaybackResult.Fail("scenario: seek rejected");

        Ended = false;    // shim: seek_prepare_locked が p->eos を消す
        double target = Math.Max(0.0, seconds);
        _generation++;    // shim: seek_prepare_locked は発行のたびに世代を進める
        if (SeekLandingDelaySeconds <= 0)
        {
            _positionSeconds = target;
            DeliverLanding();
        }
        else
        {
            _seekTargetSeconds = target;
            _seekLandingAtMilliseconds = _nowMilliseconds + Milliseconds(SeekLandingDelaySeconds);
        }

        return PlaybackResult.Ok;
    }

    public PlaybackResult Stop()
    {
        _isPaused = true;
        _seekLandingAtMilliseconds = -1;
        _loadReadyAtMilliseconds = -1;
        _settleToTargetPending = false;
        return PlaybackResult.Ok;
    }

    public PlaybackResult SetPaused(bool paused)
    {
        _isPaused = paused;
        return PlaybackResult.Ok;
    }

    public PlaybackResult SetRate(double rate)
    {
        if (rate <= 0)
            return PlaybackResult.Fail("scenario: rate must be positive");
        _rate = rate;
        return PlaybackResult.Ok;
    }

    public PlaybackResult SetRateInstant(double rate)
    {
        if (!RateApplySucceeds)
            return PlaybackResult.Fail("scenario: rate rejected");
        if (rate <= 0)
            return PlaybackResult.Fail("scenario: rate must be positive");
        _rate = rate;
        return PlaybackResult.Ok;
    }

    public void SetVolume(double volume0To100)
    {
    }

    public void SetMute(bool mute)
    {
    }

    public bool TryGetTimePos(out double seconds)
    {
        seconds = ReportPosition();
        return _hasMedia;
    }

    public bool TryGetPositionSample(out PlaybackPositionSample sample)
    {
        sample = new PlaybackPositionSample(
            ReportPosition(), PlaybackPositionBasis.Pipeline, _generation,
            _deliveredSeconds, _deliveredGeneration, _generation);
        return _hasMedia;
    }

    private double ReportPosition() =>
        Ended && EndedPositionSeconds.HasValue ? EndedPositionSeconds.Value : _positionSeconds;

    public bool TryGetDuration(out double seconds)
    {
        seconds = _durationSeconds;
        return _durationKnown;
    }

    public bool TryGetFps(out double fps)
    {
        fps = _fps;
        return _fpsKnown;
    }

    public string GetPath() => _path;

    public bool TryGetSize(out int width, out int height)
    {
        width = Width;
        height = Height;
        return width > 0 && height > 0;
    }

    public string GetVideoCodec() => _videoCodec;

    public bool IsPaused() => _isPaused;

    public bool IsSeeking() => IsLoading || HasPendingSeek;

    // ---- 予約の処理 ----

    private bool ApplyPendingSeek()
    {
        if (_seekLandingAtMilliseconds < 0 || _nowMilliseconds < _seekLandingAtMilliseconds)
            return false;

        _seekLandingAtMilliseconds = -1;
        _positionSeconds = _seekTargetSeconds + SeekOvershootSeconds;
        _settleToTargetPending = SeekOvershootSeconds != 0;
        DeliverLanding();
        return true;
    }

    private bool ApplyPendingLoad()
    {
        if (_loadReadyAtMilliseconds < 0 || _nowMilliseconds < _loadReadyAtMilliseconds)
            return false;

        _loadReadyAtMilliseconds = -1;
        _positionSeconds = _loadStartSeconds;
        _isPaused = _loadPaused;
        DeliverLanding();
        return true;
    }

    /// <summary>
    /// 再生中のフレームの配信（世代は進めない。shim の on_new_sample と同じで、
    /// 配信のたびに `latest_gen = p->generation` になる）。着地の事象は出さない。
    /// </summary>
    private void DeliverFrame()
    {
        _deliveredSeconds = _positionSeconds;
        _deliveredGeneration = _generation;
    }

    /// <summary>
    /// シーク・ロードの世代の最初のフレームの配信（着地の事象）。shim は
    /// `p->latest_gen = p->generation` と `latest_pts_ns` を更新して
    /// `get_time_pos_ex` の `delivered_generation` に載せる（tcs_gstreamer.cpp:1568-1569、3816）。
    /// </summary>
    private void DeliverLanding()
    {
        DeliverFrame();
        LandingCount++;
        Landed?.Invoke(_generation);
    }

    private void ApplyDurationArrival()
    {
        if (_durationReadyAtMilliseconds < 0 || _nowMilliseconds < _durationReadyAtMilliseconds)
            return;

        _durationReadyAtMilliseconds = -1;
        _durationKnown = true;
    }

    private void ScheduleDurationArrival()
    {
        if (DurationArrivalDelaySeconds > 0)
        {
            _durationKnown = false;
            _durationReadyAtMilliseconds = _nowMilliseconds + Milliseconds(DurationArrivalDelaySeconds);
        }
        else
        {
            _durationKnown = true;
            _durationReadyAtMilliseconds = -1;
        }
    }

    private static long Milliseconds(double seconds) => (long)Math.Round(seconds * 1000.0);
}
