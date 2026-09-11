namespace TimecodeSyncPlayer;

/// <summary>
/// ExitCoordinator が操作する終了ダイアログ。テストでは記録用の偽物を差し込む。
/// </summary>
internal interface IExitDialogHost
{
    /// <summary>確認ダイアログを表示し、閉じられるまで戻らない（既定はキャンセル）。</summary>
    void ShowConfirmation();

    /// <summary>表示中のダイアログを進捗表示へ切り替える（キャンセル・通常終了を無効化）。</summary>
    void SwitchToProgress();

    /// <summary>進捗の現在手順名を更新する。</summary>
    void UpdateStep(string stepName);

    /// <summary>ダイアログを閉じる（Coordinator 起点の終了。×で閉じた扱いにしない）。</summary>
    void CloseDialog();
}
