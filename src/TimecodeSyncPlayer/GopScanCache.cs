using System.Collections.Concurrent;

namespace TimecodeSyncPlayer;

/// <summary>0.4.5-C3: 1 素材のスキャン結果。</summary>
internal readonly record struct GopScanResult(
    int Keyframes,
    double DurationSeconds,
    double HeadGapSeconds,
    double TailGapSeconds,
    double MedianGapSeconds,
    double P95GapSeconds,
    double MaxGapSeconds)
{
    public GopSeekQuality Quality => GopScanVerdict.Judge(Keyframes, MaxGapSeconds);
}

/// <summary>
/// 0.4.5-C3: 素材のキーフレーム分布を読み込み時に 1 回だけ測り、パス単位で覚えておく。
///
/// スキャンは再生とは独立したパイプラインで行い、コンテナを読むだけでデコードしない
/// （実測: 928MB の 4K60 が 283ms）。それでも I/O は待つので、呼び出し側は UI スレッドから
/// 外して実行する。同じパスを 2 回測らない。
///
/// 測れなかった素材は <see cref="GopSeekQuality.Unknown"/> として覚え、警告を出さない
/// （誤検出より無検出が安全）。
/// </summary>
internal sealed class GopScanCache
{
    private readonly ConcurrentDictionary<string, GopScanResult> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, GopScanResult?> _scan;

    internal GopScanCache(Func<string, GopScanResult?> scan) => _scan = scan;

    /// <summary>測定済みならその結果。まだなら null（呼び出し側は警告を出さない）。</summary>
    internal GopScanResult? TryGet(string? path) =>
        !string.IsNullOrEmpty(path) && _byPath.TryGetValue(path, out GopScanResult r) ? r : null;

    /// <summary>
    /// まだ測っていなければ測る。測定済み・測定中なら何もしない。
    /// 戻り値は「このコールで測定を実行したか」。
    /// </summary>
    internal bool EnsureScanned(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (_byPath.ContainsKey(path)) return false;
        if (!_inFlight.TryAdd(path, 0)) return false;
        try
        {
            GopScanResult? result = _scan(path);
            // 失敗は「キーフレーム 0」として覚える。毎回のロードで測り直さないため。
            _byPath[path] = result ?? new GopScanResult(0, 0, 0, 0, 0, 0, 0);
            return true;
        }
        finally
        {
            _inFlight.TryRemove(path, out _);
        }
    }
}
