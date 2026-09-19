using System.Diagnostics;

namespace TimecodeSyncPlayer;

/// <summary>
/// 0.4.7: 再生の「乱れの原因になる操作」と「指示した再生速度」の台帳。
/// ネイティブ操作は必ず <see cref="Gst.GstPlaybackApi"/> を通るので、そこで記録すれば
/// 呼び出し元（手動操作・同期・ギャップ・信号断・ロード）に関係なく漏れが無い。
///
/// 使い道は「デコードが追いついていない」表示の判定（<see cref="DecodeHealthMonitor"/>）。
/// 2 秒の窓で届いたフレームが少ないとき、それがシーク・一時停止・読み込みのせいなのか、
/// 本当に復号が追いついていないのかを分けるために使う。
/// スレッド: 記録は UI スレッドと GPU 復旧経路から来うるので lock で守る。
/// </summary>
internal sealed class PlaybackActivityLedger
{
    private readonly object _gate = new();
    private readonly Func<long> _qpc;
    private readonly long _frequency;
    private long _disturbances;
    private double _rate = 1.0;
    private long _rateSinceQpc;
    private double _rateIntegralSeconds;

    public PlaybackActivityLedger(Func<long>? qpc = null, long frequency = 0)
    {
        _qpc = qpc ?? Stopwatch.GetTimestamp;
        _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        _rateSinceQpc = _qpc();
    }

    /// <summary>シーク・一時停止・再開・読み込み・停止・コマ送りが起きた回数（単調増加）。</summary>
    public long Disturbances
    {
        get { lock (_gate) return _disturbances; }
    }

    /// <summary>フレームの流れを乱す操作が起きたことを記録する。</summary>
    public void NoteDisturbance()
    {
        lock (_gate) _disturbances++;
    }

    /// <summary>再生速度が変わった（受け付けられた）ことを記録する。</summary>
    public void NoteRate(double rate)
    {
        if (!double.IsFinite(rate) || rate <= 0) return;
        lock (_gate)
        {
            AccumulateLocked(_qpc());
            _rate = rate;
        }
    }

    /// <summary>
    /// 指示した再生速度の時間積分（秒）。2 つの時点の差を経過時間で割ると、その間の平均速度になる。
    /// </summary>
    public double RateIntegralSeconds()
    {
        lock (_gate)
        {
            AccumulateLocked(_qpc());
            return _rateIntegralSeconds;
        }
    }

    private void AccumulateLocked(long now)
    {
        if (now > _rateSinceQpc)
            _rateIntegralSeconds += _rate * (now - _rateSinceQpc) / (double)_frequency;
        _rateSinceQpc = now;
    }
}
