namespace TimecodeSyncPlayer;

/// <summary>全画面に入る要求の出どころ（v0.6.6 R-8）。</summary>
internal enum FullscreenRequestOrigin
{
    /// <summary>利用者が FULLSCREEN のボタンを押した（マウス・キーボード・UI オートメーションの Invoke）。</summary>
    UserButton,

    /// <summary>
    /// 人が操作していない経路（起動時の引数・プロジェクトの読み込み・設定の復元・デバイスの復旧や表示の変化のあとの戻し）。
    /// v0.6.6 の時点でこの経路で全画面に入る所は無い。足すときはこれを渡し、本番の無人の立ち上げを確認で止めない。
    /// </summary>
    Automatic,
}

/// <summary>
/// 全画面を出す前に確認を出すかの判定（v0.6.6 R-8）。
/// 主画面（作業中の画面、表示名に「(Primary)」）に、利用者のボタンの操作で出すときだけ確認する。
/// 「今後表示しない」は無い（毎回出す、利用者の決定）。
/// </summary>
internal static class FullscreenConfirmationPolicy
{
    public static bool ShouldConfirm(DisplayTarget? target, FullscreenRequestOrigin origin) =>
        origin == FullscreenRequestOrigin.UserButton && target is { IsPrimary: true };
}
