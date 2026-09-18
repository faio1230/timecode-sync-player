using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

/// <summary>IPlaybackApi の記録用 fake。呼び出し内容と結果を注入できる。</summary>
internal sealed class FakePlaybackApi : IPlaybackApi
{
    public List<(string Path, double? StartSeconds, bool Paused)> Loads { get; } = [];
    public List<double> Seeks { get; } = [];
    public List<bool> SetPausedCalls { get; } = [];
    public List<double> SetRates { get; } = [];
    public List<double> SetRateInstantCalls { get; } = [];
    public List<double> Volumes { get; } = [];
    public List<bool> Mutes { get; } = [];

    public PlaybackResult LoadResult { get; set; } = PlaybackResult.Ok;
    public PlaybackResult SeekResult { get; set; } = PlaybackResult.Ok;
    public PlaybackResult StopResult { get; set; } = PlaybackResult.Ok;
    public PlaybackResult SetPausedResult { get; set; } = PlaybackResult.Ok;
    public PlaybackResult SetRateResult { get; set; } = PlaybackResult.Ok;
    public PlaybackResult SetRateInstantResult { get; set; } = PlaybackResult.Ok;

    public string Path { get; set; } = "";
    public double TimePos { get; set; }
    public double Duration { get; set; }
    public double Fps { get; set; }
    public bool Paused { get; set; }
    public bool Seeking { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string VideoCodec { get; set; } = "";

    public PlaybackResult Load(string path, double? startSeconds, bool paused)
    {
        Loads.Add((path, startSeconds, paused));
        return LoadResult;
    }

    public PlaybackResult Seek(double seconds)
    {
        Seeks.Add(seconds);
        return SeekResult;
    }

    public PlaybackResult Stop() => StopResult;

    public PlaybackResult SetPaused(bool paused)
    {
        SetPausedCalls.Add(paused);
        return SetPausedResult;
    }

    public PlaybackResult SetRate(double rate)
    {
        SetRates.Add(rate);
        return SetRateResult;
    }

    public PlaybackResult SetRateInstant(double rate)
    {
        SetRateInstantCalls.Add(rate);
        return SetRateInstantResult;
    }

    public void SetVolume(double volume0To100) => Volumes.Add(volume0To100);

    public void SetMute(bool mute) => Mutes.Add(mute);

    public bool TryGetTimePos(out double seconds)
    {
        seconds = TimePos;
        return true;
    }

    public bool TryGetPositionSample(out PlaybackPositionSample sample)
    {
        sample = new PlaybackPositionSample(
            TimePos, PlaybackPositionBasis.Pipeline, 0, 0, 0, 0);
        return true;
    }

    public bool TryGetDuration(out double seconds)
    {
        seconds = Duration;
        return true;
    }

    public bool TryGetFps(out double fps)
    {
        fps = Fps;
        return true;
    }

    public string GetPath() => Path;

    public bool TryGetSize(out int width, out int height)
    {
        width = Width;
        height = Height;
        return width > 0 && height > 0;
    }

    public string GetVideoCodec() => VideoCodec;

    public bool IsPaused() => Paused;

    public bool IsSeeking() => Seeking;
}
