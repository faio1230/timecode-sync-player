using System.Diagnostics;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// 0.4.5-A: 着地未確認（配信世代 &lt; 現在世代）の間だけ「最後の配信 PTS + 経過時間 × 実測レート」で
/// 位置を外挿する。着地済みは与えられた <see cref="PlaybackPositionSample.Seconds"/> をそのまま使う。
/// 外挿の上限は素材のフレーム 2 枚ぶん（60fps で 33ms、25fps で 80ms）。実時間 0.5 秒のような
/// 大きな上限は、シーク中に育つ誤差を外挿そのものが埋めてしまうため使わない。
/// 純ロジック（QPC は注入可能）。フェーズ 1 では判断に使わず、trace へ併記するだけ。
/// </summary>
internal sealed class PlaybackPositionFeedback
{
    /// <summary>外挿の上限（素材フレーム枚数）。</summary>
    internal const double CapFrames = 2.0;

    /// <summary>レート実測に使う最小の配信間隔（秒）。</summary>
    internal const double MinRateIntervalSeconds = 0.020;

    /// <summary>レート実測として採用する範囲と、EMA の重み（シーク学習と同じ keep 0.7）。</summary>
    internal const double MinRate = 0.25;
    internal const double MaxRate = 4.0;
    internal const double RateKeep = 0.7;

    /// <summary>fps 不明時の既定（シーク判定と同じ 30）。</summary>
    internal const double DefaultVideoFps = 30.0;

    private readonly Func<long> _qpc;
    private readonly long _frequency;
    private bool _hasDelivered;
    private double _lastDeliveredSeconds;
    private ulong _lastDeliveredGeneration;
    private long _lastDeliveredQpc;
    private bool _hasRate;
    private double _rate = 1.0;

    public PlaybackPositionFeedback(Func<long>? qpc = null, long frequency = 0)
    {
        _qpc = qpc ?? Stopwatch.GetTimestamp;
        _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
    }

    /// <summary>実測レート（未計測は 1.0）。</summary>
    public double Rate => _rate;

    /// <summary>レートを実測できたか。</summary>
    public bool HasMeasuredRate => _hasRate;

    /// <summary>素材が変わる（ロード）ときに捨てる。</summary>
    public void Reset()
    {
        _hasDelivered = false;
        _lastDeliveredSeconds = 0;
        _lastDeliveredGeneration = 0;
        _lastDeliveredQpc = 0;
        _hasRate = false;
        _rate = 1.0;
    }

    /// <summary>1 回の位置サンプルから評価位置を求める（状態も更新する）。</summary>
    public PlaybackPositionReading Observe(in PlaybackPositionSample sample, double videoFps)
    {
        long now = _qpc();
        bool deliveredPresent = sample.DeliveredSeconds > 0 && sample.DeliveredGeneration > 0;
        bool deliveredChanged = deliveredPresent &&
            (!_hasDelivered ||
             sample.DeliveredSeconds != _lastDeliveredSeconds ||
             sample.DeliveredGeneration != _lastDeliveredGeneration);

        if (deliveredPresent && _hasDelivered && deliveredChanged)
        {
            double dt = (now - _lastDeliveredQpc) / (double)_frequency;
            double dp = sample.DeliveredSeconds - _lastDeliveredSeconds;
            if (sample.DeliveredGeneration < _lastDeliveredGeneration || dp < 0)
            {
                // 逆行（ロード・巻き戻し）。レートの系列を捨てる。
                _hasRate = false;
                _rate = 1.0;
            }
            else if (dp > 0 && dt >= MinRateIntervalSeconds)
            {
                double measured = dp / dt;
                if (measured is >= MinRate and <= MaxRate)
                {
                    _rate = _hasRate ? (_rate * RateKeep) + (measured * (1.0 - RateKeep)) : measured;
                    _hasRate = true;
                }
            }
        }
        if (deliveredChanged)
        {
            _lastDeliveredSeconds = sample.DeliveredSeconds;
            _lastDeliveredGeneration = sample.DeliveredGeneration;
            _lastDeliveredQpc = now;
            _hasDelivered = true;
        }

        bool unlanded = deliveredPresent && sample.DeliveredGeneration < sample.CurrentGeneration;
        double evaluationSeconds;
        PlaybackPositionBasis basis;
        if (unlanded && _hasDelivered)
        {
            double elapsed = Math.Max(0.0, (now - _lastDeliveredQpc) / (double)_frequency);
            double cap = CapFrames / ResolveFps(videoFps);
            evaluationSeconds = _lastDeliveredSeconds + (Math.Min(elapsed, cap) * _rate);
            basis = PlaybackPositionBasis.Delivered;
        }
        else
        {
            evaluationSeconds = sample.Seconds;
            basis = sample.Basis;
        }

        bool landingConfirmed = deliveredPresent &&
            sample.DeliveredGeneration >= sample.CurrentGeneration;
        return new PlaybackPositionReading(evaluationSeconds, basis, landingConfirmed);
    }

    private static double ResolveFps(double videoFps) =>
        double.IsFinite(videoFps) && videoFps > 0 ? videoFps : DefaultVideoFps;
}

/// <summary>0.4.5-A: 評価位置（フェーズ 1 では trace に併記するだけ）。</summary>
internal readonly record struct PlaybackPositionReading(
    double EvaluationSeconds,
    PlaybackPositionBasis Basis,
    bool LandingConfirmed);
