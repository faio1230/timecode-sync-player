namespace TimecodeSyncPlayer.Output;

/// <summary>
/// v0.6.4 段 3（設計書 2-2）: GPU 経路で、出力（OutputEngine の GPU worker）が新しいソースフレームを
/// 合成した数を窓ごとに数える。`Playback perf` の composedSourceFrames。
/// 「新しい」は、描いたソースフレームの（世代, 通番）の組が前回数えたものと違うこと。同じフレームの
/// 再合成（保持中のリースの再利用・Held・黒・Freeze・Present だけの tick）は数えない。黒の判定に使うのは
/// この数（窓の間に 0 なら、出力は新しい絵を 1 枚も描いていない）。
/// GPU worker だけが RecordCompose を呼び（前回の組はそのスレッドだけが触る）、UI スレッドが Take で
/// 取り出す。数は Interlocked の加算と交換だけで、ロックも待ちも足さない。
/// </summary>
internal sealed class ComposedSourceFrameCounter
{
    private long _windowCount;
    private int _lastGeneration = int.MinValue;
    private long _lastSequence = long.MinValue;

    /// <summary>
    /// 合成 1 回分を記録する（GPU worker）。drewSourceFrame はソースフレームを描いた tick だけ true
    /// （ギャップ中や取得できなかった tick は false）。
    /// </summary>
    public void RecordCompose(bool drewSourceFrame, int generation, long sequence)
    {
        if (!drewSourceFrame)
            return;
        if (generation == _lastGeneration && sequence == _lastSequence)
            return;
        _lastGeneration = generation;
        _lastSequence = sequence;
        Interlocked.Increment(ref _windowCount);
    }

    /// <summary>前回からの数を返して数え直す（UI スレッド）。</summary>
    public long Take() => Interlocked.Exchange(ref _windowCount, 0);
}
