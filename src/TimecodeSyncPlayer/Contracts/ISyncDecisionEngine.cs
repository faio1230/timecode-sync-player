namespace TimecodeSyncPlayer;

public interface ISyncDecisionEngine
{
    SyncDecision Decide(double ltcSeconds, SyncPlaybackState state);

    /// <summary>
    /// D37-a: 粗い判定のゲート履歴を切る。シークの発行・ロード・手動移動の後に呼ぶ
    /// （既定実装は何もしない。ゲートを持たない実装・テスト用のフェイクのため）。
    /// </summary>
    void ResetSeekGate()
    {
    }
}
