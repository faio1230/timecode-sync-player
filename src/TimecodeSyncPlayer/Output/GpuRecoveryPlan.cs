namespace TimecodeSyncPlayer.Output;

/// <summary>段階 5.2 の復旧手順。OutputEngine はこの順序で実行する。</summary>
internal enum GpuRecoveryStep
{
    StopSpoutWorker,
    ReleaseLeasesAndDisposeComposeResources,
    RecreateDevice,
    RebuildComposeResources,
    RecreateFullscreenSwapchain,
    ReinitializeSpout,
    ReconnectSources,
}

/// <summary>
/// 復旧手順の順序（段階 5.2）。Spout worker 停止が最初、ソース再接続が最後。
/// デバイス・リース・スワップチェーンに触れない管理テスト向けの唯一の定義。
/// </summary>
internal static class GpuRecoveryPlan
{
    public static IReadOnlyList<GpuRecoveryStep> Steps { get; } =
    [
        GpuRecoveryStep.StopSpoutWorker,
        GpuRecoveryStep.ReleaseLeasesAndDisposeComposeResources,
        GpuRecoveryStep.RecreateDevice,
        GpuRecoveryStep.RebuildComposeResources,
        GpuRecoveryStep.RecreateFullscreenSwapchain,
        GpuRecoveryStep.ReinitializeSpout,
        GpuRecoveryStep.ReconnectSources,
    ];
}
