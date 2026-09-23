using System.Diagnostics;

namespace TimecodeSyncPlayer.Output;

/// <summary>2 秒窓などの区間ごとに取り出す「同じ絵を出した tick」の内訳。</summary>
internal readonly record struct OutputHoldSnapshot(
    int NewFrameTicks,
    int FencePendingTicks,
    int NoNewFrameTicks,
    double LongestHeldMs,
    double MaxFenceWaitMs);

/// <summary>
/// 0.4.8: 合成が新しいソースフレームを描かずに前の絵（Held）を出した tick を、理由ごとに数える。
/// 「Spout の送信は続いているのに中身が変わらない」を、出力トレース無しの通常ログで追えるようにする。
/// <list type="bullet">
/// <item>FencePending: shim のリングへのコピー完了（共有フェンス）を待っていた（shim 側の GPU が遅れている）</item>
/// <item>NoNewFrame: shim から新しいフレームが届いていなかった（復号・配信が止まっている）</item>
/// </list>
/// ギャップ中（Freeze/Black/Hold）の tick は数えない（仕様どおりの静止）。GPU worker が書き、UI スレッドが取り出す。
/// </summary>
internal sealed class OutputHoldCounter
{
    private readonly object _gate = new();
    private int _newFrameTicks;
    private int _fencePendingTicks;
    private int _noNewFrameTicks;
    private bool _holding;
    private long _heldSinceQpc;
    private double _longestHeldMs;
    private double _maxFenceWaitMs;

    /// <summary>合成 tick 1 回分を記録する。</summary>
    public void RecordTick(bool drewNewFrame, bool fencePending, long nowQpc)
    {
        lock (_gate)
        {
            if (drewNewFrame)
            {
                _newFrameTicks++;
                CloseHeld(nowQpc);
                return;
            }
            if (fencePending) _fencePendingTicks++;
            else _noNewFrameTicks++;
            if (!_holding) { _holding = true; _heldSinceQpc = nowQpc; }
        }
    }

    /// <summary>ギャップなど、Held を数えない tick。続いていた Held はここで切る。</summary>
    public void RecordExcludedTick(long nowQpc)
    {
        lock (_gate) CloseHeld(nowQpc);
    }

    /// <summary>フェンス待ちが終わって描けたときの待ち時間。</summary>
    public void RecordFenceWait(double waitedMs)
    {
        lock (_gate) _maxFenceWaitMs = Math.Max(_maxFenceWaitMs, waitedMs);
    }

    /// <summary>前回からの内訳を返して数え直す（続いている Held は今の時点までの長さで数える）。</summary>
    public OutputHoldSnapshot Take(long nowQpc)
    {
        lock (_gate)
        {
            double longest = _longestHeldMs;
            if (_holding)
                longest = Math.Max(longest, (nowQpc - _heldSinceQpc) * 1000.0 / Stopwatch.Frequency);
            var snapshot = new OutputHoldSnapshot(_newFrameTicks, _fencePendingTicks, _noNewFrameTicks, longest, _maxFenceWaitMs);
            _newFrameTicks = 0;
            _fencePendingTicks = 0;
            _noNewFrameTicks = 0;
            _longestHeldMs = 0;
            _maxFenceWaitMs = 0;
            if (_holding) _heldSinceQpc = nowQpc;
            return snapshot;
        }
    }

    private void CloseHeld(long nowQpc)
    {
        if (!_holding) return;
        _longestHeldMs = Math.Max(_longestHeldMs, (nowQpc - _heldSinceQpc) * 1000.0 / Stopwatch.Frequency);
        _holding = false;
    }
}
