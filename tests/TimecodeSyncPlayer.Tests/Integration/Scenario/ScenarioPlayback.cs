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

    /// <summary>テストが位置を直接与える（旧 harness の AdvancePlayback と同じ）。</summary>
    public void SetPosition(double seconds)
    {
        _positionSeconds = seconds;
        _generation++;
    }

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
    }

    // ---- IPlaybackApi ----

    public PlaybackResult Load(string path, double? startSeconds, bool paused)
    {
        if (!LoadSucceeds)
            return PlaybackResult.Fail("scenario: load rejected");

        _path = path;
        _hasMedia = true;
        _generation++;
        double start = Math.Max(0.0, startSeconds ?? 0.0);

        if (LoadDurationSeconds <= 0)
        {
            _positionSeconds = start;
            _isPaused = paused;
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

        double target = Math.Max(0.0, seconds);
        if (SeekLandingDelaySeconds <= 0)
        {
            _positionSeconds = target;
            _generation++;
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
        seconds = _positionSeconds;
        return _hasMedia;
    }

    public bool TryGetPositionSample(out PlaybackPositionSample sample)
    {
        sample = new PlaybackPositionSample(
            _positionSeconds, PlaybackPositionBasis.Pipeline, _generation, 0, 0, _generation);
        return _hasMedia;
    }

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
        _generation++;
        return true;
    }

    private bool ApplyPendingLoad()
    {
        if (_loadReadyAtMilliseconds < 0 || _nowMilliseconds < _loadReadyAtMilliseconds)
            return false;

        _loadReadyAtMilliseconds = -1;
        _positionSeconds = _loadStartSeconds;
        _isPaused = _loadPaused;
        _generation++;
        return true;
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
