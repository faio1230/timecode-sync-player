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

    /// <summary>
    /// D37-b: シーク 1 回の実測所要（秒）を伝える。この値以内の不足はシークではなく
    /// 速度補正に任せる（既定実装は何もしない）。
    /// </summary>
    void UpdateSeekCostSeconds(double seconds)
    {
    }

    /// <summary>
    /// D37-b: 位置を信用できないフレーム（シーク保留中・時間切れ後の再確認中）の決定。
    /// 呼び出し側は要求を Deferred のまま維持する（既定実装は None）。
    /// </summary>
    SyncDecision WhilePositionUntrusted(SyncPlaybackState state) => SyncDecision.None;
}
