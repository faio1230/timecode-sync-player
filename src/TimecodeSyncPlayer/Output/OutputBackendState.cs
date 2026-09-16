using Serilog;

namespace TimecodeSyncPlayer.Output;

/// <summary>起動時に決めた出力バックエンド。</summary>
public readonly record struct OutputBackendDecision(
    OutputBackend Requested,
    OutputBackend Effective,
    bool PlaybackAvailable,
    string Detail);

/// <summary>設定値と D3D11.4 の検出結果から実際に使う出力バックエンドを決める。</summary>
internal static class OutputBackendResolver
{
    public static OutputBackendDecision Resolve(
        OutputBackend requested,
        Func<D3D11CapabilityResult> probe)
    {
        // Gpu が使えないときに黙って落とす先はもう無い。再生可否は PlaybackAvailable で伝え、
        // 見せ方は MainWindow が 1 か所で決める（R1 1-2）。
        D3D11CapabilityResult capability = probe();
        return new(requested, OutputBackend.Gpu, capability.Supported, capability.Detail);
    }
}

/// <summary>
/// 起動時に検出した有効な出力バックエンドを保持する。設定の要求値とは独立で、
/// 利用不可でも設定ファイルは書き換えない。
/// </summary>
public sealed class OutputBackendState
{
    /// <summary>E2E・検証用の注入。設定項目は増やさない（R1 1-2）。</summary>
    internal const string ForceUnavailableEnvironmentVariable = "TIMECODE_SYNC_PLAYER_FORCE_GPU_UNAVAILABLE";

    // 初期化前は「要求値も有効値も Gpu」のプレースホルダ。エンジン生成は IsInitialized で
    // 判定するため、MainWindow を初期化せず構築する単体テストでも GPU 出力を開始しない。
    public OutputBackendDecision Decision { get; private set; } =
        new(OutputBackend.Gpu, OutputBackend.Gpu, true, "未初期化");

    /// <summary>起動時の Initialize が完了したか。false の間は出力エンジンを生成しない。</summary>
    public bool IsInitialized { get; private set; }

    public OutputBackend Effective => Decision.Effective;

    /// <summary>false のときは再生（ロード・シーク・LTC 同期）を行わない。</summary>
    public bool PlaybackAvailable => Decision.PlaybackAvailable;

    public void Initialize(OutputBackend requested) => Initialize(
        requested,
        D3D11CapabilityProbe.Detect,
        IsForcedUnavailable(Environment.GetEnvironmentVariable(ForceUnavailableEnvironmentVariable)));

    internal void Initialize(OutputBackend requested, Func<D3D11CapabilityResult> probe)
        => Initialize(requested, probe, forceUnavailable: false);

    internal void Initialize(
        OutputBackend requested,
        Func<D3D11CapabilityResult> probe,
        bool forceUnavailable)
    {
        Decision = forceUnavailable
            ? new OutputBackendDecision(requested, OutputBackend.Gpu, false,
                "検証用の注入: " + ForceUnavailableEnvironmentVariable + " により GPU 出力を利用不可にしました。")
            : OutputBackendResolver.Resolve(requested, probe);
        IsInitialized = true;

        if (!Decision.PlaybackAvailable)
        {
            Log.Error(
                "OutputBackend: Gpu 出力を利用できないため再生を無効にします: {Detail}",
                Decision.Detail);
        }
        else
        {
            Log.Information("OutputBackend: Gpu（{Detail}）", Decision.Detail);
        }
    }

    internal static bool IsForcedUnavailable(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().ToLowerInvariant() is not ("0" or "false" or "no");
}
