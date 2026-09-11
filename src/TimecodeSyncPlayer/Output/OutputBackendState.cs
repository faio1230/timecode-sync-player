using Serilog;

namespace TimecodeSyncPlayer.Output;

/// <summary>起動時に決めた出力バックエンド。</summary>
internal readonly record struct OutputBackendDecision(
    OutputBackend Requested,
    OutputBackend Effective,
    bool FallbackApplied,
    string Detail);

/// <summary>設定値と D3D11.4 の検出結果から実際に使う出力バックエンドを決める。</summary>
internal static class OutputBackendResolver
{
    public static OutputBackendDecision Resolve(
        OutputBackend requested,
        Func<D3D11CapabilityResult> probe)
    {
        if (requested == OutputBackend.Cpu)
            return new(requested, OutputBackend.Cpu, false, "Cpu が指定されています。");

        D3D11CapabilityResult capability = probe();
        return capability.Supported
            ? new(requested, OutputBackend.Gpu, false, capability.Detail)
            : new(requested, OutputBackend.Cpu, true, capability.Detail);
    }
}

/// <summary>
/// 起動時に検出した有効な出力バックエンドを保持する。設定の要求値とは独立で、
/// フォールバック時も設定ファイルは書き換えない。
/// </summary>
internal sealed class OutputBackendState
{
    public OutputBackendDecision Decision { get; private set; } =
        new(OutputBackend.Cpu, OutputBackend.Cpu, false, "未初期化");

    public OutputBackend Effective => Decision.Effective;

    public void Initialize(OutputBackend requested)
        => Initialize(requested, D3D11CapabilityProbe.Detect);

    internal void Initialize(OutputBackend requested, Func<D3D11CapabilityResult> probe)
    {
        Decision = OutputBackendResolver.Resolve(requested, probe);
        if (Decision.FallbackApplied)
        {
            Log.Warning(
                "OutputBackend: Gpu を指定されましたが利用できないため Cpu へフォールバックします: {Detail}",
                Decision.Detail);
        }
        else if (Decision.Effective == OutputBackend.Gpu)
        {
            Log.Information("OutputBackend: Gpu（{Detail}）", Decision.Detail);
        }
        else
        {
            Log.Information("OutputBackend: Cpu（{Detail}）", Decision.Detail);
        }
    }
}
