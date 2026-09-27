namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.4 K3 (3): シークの時間予算の唯一の源。着地の時間切れ
/// （<see cref="LandingTimeoutSeconds"/>）を 1 つの定数にし、ギャップのフリーズの確定
/// （<see cref="GapFreezeHandler.TimeoutSec"/>）、着地の状態
/// （<see cref="TimecodeSyncSeekState.LandingSafetyTimeout"/>）、shim へ渡すポンプの予算
/// （<see cref="PumpBudgetMilliseconds"/>）をここから導く。ポンプの実測スパンは最大 192ms で、
/// 着地 3.0 秒・ポンプ 2.5 秒の余裕は足りている（docs/design/v0.5.4-k3-a1.md §9-3）。
/// </summary>
internal static class SeekTimeBudget
{
    /// <summary>着地の時間切れ（秒）。時間切れの定数はこの 1 つだけ。</summary>
    public const double LandingTimeoutSeconds = 3.0;

    /// <summary>shim のポンプを着地の時間切れより先に切る余白（秒）。</summary>
    public const double PumpBudgetMarginSeconds = 0.5;

    /// <summary>shim へ渡すポンプの予算（ms）= 着地の時間切れ − 余白。</summary>
    public const int PumpBudgetMilliseconds =
        (int)((LandingTimeoutSeconds - PumpBudgetMarginSeconds) * 1000);

    /// <summary>着地の時間切れ（<see cref="LandingTimeoutSeconds"/> と同じ値）。</summary>
    public static readonly TimeSpan LandingTimeout = TimeSpan.FromSeconds(LandingTimeoutSeconds);

    /// <summary>shim のポンプの予算を渡す環境変数（shim は DLL ロード時に 1 回だけ読む）。</summary>
    public const string PumpBudgetEnvironmentVariable = "TCS_PUMP_BUDGET_MS";
}
