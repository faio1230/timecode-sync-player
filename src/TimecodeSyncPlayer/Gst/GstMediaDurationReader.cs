using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// v0.6.6 F-7: プレイリストのクリップの長さを shim の軽い関数（容器の長さだけを問い合わせる）で読む。
/// ffprobe は使わない（開発機・検証機と利用者の機体で経路が分かれないように）。
/// </summary>
/// <remarks>
/// 呼び出しはスレッドプールで行う（UI スレッドをふさがない）。呼び出し側の await の続きは
/// 呼び出し側の同期コンテキストに戻るので、書き戻し（<c>PlaylistState.UpdateMediaDuration</c>）は
/// 今と同じく UI スレッドで行われる。取れなかったときは null を返し、長さは 0 のまま
/// （読み込んだときに再生時の長さで埋める。<see cref="PlaylistDurationFallback"/>）。
/// </remarks>
internal sealed class GstMediaDurationReader : IMediaDurationReader
{
    private readonly Func<string, double?> _probe;

    public GstMediaDurationReader()
        : this(GstPlaybackApi.ProbeMediaDuration)
    {
    }

    internal GstMediaDurationReader(Func<string, double?> probe)
    {
        _probe = probe;
    }

    public async Task<TimeSpan?> ReadDurationAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        double? seconds = await Task.Run(() => _probe(filePath)).ConfigureAwait(true);
        if (seconds is double s && s > 0 && !double.IsInfinity(s))
            return TimeSpan.FromSeconds(s);
        return null;
    }
}
