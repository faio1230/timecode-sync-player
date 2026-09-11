namespace TimecodeSyncPlayer.Output;

/// <summary>
/// Spout 段階リングの純粋状態機械（段階 3）。合成スレッドが BeginStage/CompleteStage、
/// Spout worker が TryBeginSend/EndSend を呼ぶ。呼び出し側の lock 前提で、GPU には触れない。
/// 送信失敗時は Ready に戻して保持画像として再送し、より新しい画像が待っていれば解放する。
/// </summary>
internal sealed class SpoutStageRing
{
    private enum StageState { Free, Staging, Ready, Sending }

    private readonly StageState[] states;
    private readonly ImageStamp[] stamps;
    private int ready = -1;

    public SpoutStageRing(int capacity)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        states = new StageState[capacity];
        stamps = new ImageStamp[capacity];
    }

    /// <summary>最後に SendTexture できた画像（再送判断とログ用）。</summary>
    public ImageStamp Held { get; private set; }

    public int Capacity => states.Length;
    public int ReadyCount => states.Count(state => state == StageState.Ready);
    public int FreeCount => states.Count(state => state == StageState.Free);

    /// <summary>書込み先を確保する。空きが無ければ古い Ready を再利用し、それも無ければ -1。</summary>
    public int BeginStage(ImageStamp stamp)
    {
        int slot = Array.IndexOf(states, StageState.Free);
        if (slot < 0)
        {
            if (ready < 0) return -1;
            slot = ready; // 新しい画像で上書きする（送信前の古い Ready を破棄）。
            ready = -1;
        }
        states[slot] = StageState.Staging;
        stamps[slot] = stamp;
        return slot;
    }

    /// <summary>GPU コピー発行後に Ready へ。同時に Ready は常に最新 1 枚だけにする。</summary>
    public void CompleteStage(int slot, ImageStamp stamp)
    {
        if (ready >= 0 && ready != slot) states[ready] = StageState.Free;
        stamps[slot] = stamp;
        states[slot] = StageState.Ready;
        ready = slot;
    }

    /// <summary>コピー発行に失敗した場合の巻き戻し。</summary>
    public void AbortStage(int slot) => states[slot] = StageState.Free;

    /// <summary>最新の Ready を送信対象にする。無ければ -1。</summary>
    public int TryBeginSend(out ImageStamp stamp)
    {
        if (ready < 0) { stamp = default; return -1; }
        int slot = ready;
        ready = -1;
        states[slot] = StageState.Sending;
        stamp = stamps[slot];
        return slot;
    }

    /// <summary>送信終了。成功なら解放、失敗なら保持画像へ戻す（より新しい Ready があれば解放）。</summary>
    public void EndSend(int slot, ImageStamp stamp, bool sent)
    {
        if (sent)
        {
            Held = stamp;
            states[slot] = StageState.Free;
            return;
        }
        if (ready < 0) { states[slot] = StageState.Ready; ready = slot; }
        else states[slot] = StageState.Free;
    }
}
