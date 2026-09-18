using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer;

/// <summary>0.4.5-C: ロング GOP 警告モニタの観測遷移。</summary>
internal enum LongGopWarningTransition
{
    None,
    /// <summary>このトラックで初めて警告が確定した（ログ・トレース・プレイリスト印はここで 1 回）。</summary>
    Latch,
    /// <summary>ラッチ済みで実測間隔が伸びた（文言の「実測 X.X 秒」更新のみ）。</summary>
    IntervalUpdated,
}

/// <summary>
/// 0.4.5-C: ロング GOP 警告の表示状態（純ロジック、UI・GStreamer 非依存）。
/// トラック単位で単調: 一度ラッチしたらシーク・一時停止・再ロードでは消えない。
/// トラック切替で観測をリセットし、既に印があるトラックは表示状態から始める。
/// </summary>
internal sealed class LongGopWarningMonitor
{
    public const int StateMeasuring = 0;
    public const int StateWarning = 1;

    private Guid? _trackId;
    private bool _warningActive;
    private double _measuredSeconds;

    public Guid? TrackId => _trackId;

    /// <summary>警告を表示すべきか（ラッチ済み、又は印付きトラックの再ロード直後）。</summary>
    public bool IsWarningActive => _warningActive;

    /// <summary>確定したキーフレーム間隔（秒）。0 = 未確定。</summary>
    public double MeasuredSeconds => _measuredSeconds;

    public LongGopWarningTransition Observe(Guid? trackId, GopStatus? status, bool trackAlreadyMarked)
    {
        if (trackId != _trackId)
        {
            _trackId = trackId;
            _warningActive = trackId.HasValue && trackAlreadyMarked;
            _measuredSeconds = 0;
        }

        if (!trackId.HasValue)
        {
            _warningActive = false;
            return LongGopWarningTransition.None;
        }

        if (status is not { Active: true } sample)
            return LongGopWarningTransition.None;

        bool measuredIncreased = sample.MaxIntervalSeconds > _measuredSeconds;
        if (measuredIncreased)
            _measuredSeconds = sample.MaxIntervalSeconds;

        if (!_warningActive && sample.State == StateWarning)
        {
            _warningActive = true;
            return LongGopWarningTransition.Latch;
        }

        if (_warningActive && measuredIncreased)
            return LongGopWarningTransition.IntervalUpdated;

        return LongGopWarningTransition.None;
    }
}

/// <summary>0.4.5-C: 警告文言（ステータス行とプレイリスト行で共有）。</summary>
internal static class LongGopWarningMessages
{
    public const string Recommendation =
        "キーフレーム間隔が長いため同期が不安定になることがあります（推奨: 1〜2 秒）";

    public static string Format(double measuredSeconds) =>
        measuredSeconds > 0
            ? FormattableString.Invariant($"{Recommendation}（実測 {measuredSeconds:F1} 秒）")
            : Recommendation;
}
