using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Erytheia.Output;

internal enum GpuPlacementLogLevel { Information, Warning }

internal readonly record struct GpuPlacementLogEntry(GpuPlacementLogLevel Level, string Text);

/// <summary>判定 1 回の結果。ログの行は呼び出し側が書く（単体の試験で行を確かめるため）。</summary>
internal sealed record GpuPlacementReport(
    GpuPlacementInput Input,
    GpuPlacementResult Result,
    IReadOnlyList<GpuPlacementLogEntry> Logs,
    string? WarningText,
    string? NameText,
    string? WarningToolTip = null);

/// <summary>アダプターの一覧と 2 つの並び。UI の側で、合成のデバイスとは別の DXGI の Factory を作って取る。</summary>
internal interface IGpuAdapterOrderSource
{
    GpuAdapterOrder Read();
}

/// <summary>
/// v0.6.8: 材料を集めて <see cref="GpuPlacementPolicy.Evaluate"/> を呼び、ログの行と表示の文字を作る。
/// 呼ぶのは起動（WaitForDevice の後）と Recovered の後の UI のスレッドの 1 回だけ（合成の周期には入れない）。
/// 材料の取得から文字の組み立てまで、どこで例外が出ても NotJudged（error）にして、起動と再生は続ける。
/// 表示の反映とログの書き出しは呼び出し側（MainWindow）なので、そちらでも囲む。
/// </summary>
internal static class GpuPlacementJudge
{
    public static GpuPlacementReport Run(
        GpuPlacementTiming timing,
        bool playbackAvailable,
        bool deviceReady,
        Func<GpuDeviceIdentity?> readDevice,
        IGpuAdapterOrderSource orders,
        Func<bool> isRemoteSession,
        bool forced)
    {
        try
        {
            return RunCore(timing, playbackAvailable, deviceReady, readDevice, orders, isRemoteSession, forced);
        }
        catch (Exception e)
        {
            // 材料の後（行・名前・文面の組み立て）の例外。材料を持たない入力で行を作り直す。
            var input = new GpuPlacementInput(timing, playbackAvailable, deviceReady, null, null, false, forced);
            GpuPlacementResult result = GpuPlacementPolicy.Failed();
            return new(input, result,
            [
                new(GpuPlacementLogLevel.Warning, $"GPU placement failed: {e.GetType().Name}: {e.Message}"),
                new(GpuPlacementLogLevel.Information,
                    GpuPlacementPolicy.LogPrefix + GpuPlacementPolicy.FormatLogFields(input, result)),
            ], null, null);
        }
    }

    private static GpuPlacementReport RunCore(
        GpuPlacementTiming timing,
        bool playbackAvailable,
        bool deviceReady,
        Func<GpuDeviceIdentity?> readDevice,
        IGpuAdapterOrderSource orders,
        Func<bool> isRemoteSession,
        bool forced)
    {
        var logs = new List<GpuPlacementLogEntry>();
        var input = new GpuPlacementInput(timing, playbackAvailable, deviceReady, null, null, false, forced);
        GpuPlacementResult result;
        if (!playbackAvailable || !deviceReady)
        {
            result = GpuPlacementPolicy.Evaluate(input);
        }
        else
        {
            try
            {
                GpuDeviceIdentity? device = readDevice();
                input = input with { Device = device };
                input = input with { RemoteSession = isRemoteSession() };
                if (device is not null)
                    input = input with { Order = orders.Read() };
                result = GpuPlacementPolicy.Evaluate(input);
            }
            catch (Exception e)
            {
                logs.Add(new(GpuPlacementLogLevel.Warning,
                    $"GPU placement failed: {e.GetType().Name}: {e.Message}"));
                result = GpuPlacementPolicy.Failed();
            }
        }

        if (result.Reason == "forced")
        {
            logs.Add(new(GpuPlacementLogLevel.Warning,
                "検証用の注入: " + GpuPlacementPolicy.ForceWarningEnvironmentVariable +
                " により GPU の置き場の判定を IntegratedInUse にしました（画面の文面は本物のまま）"));
        }
        logs.Add(new(GpuPlacementLogLevel.Information,
            GpuPlacementPolicy.LogPrefix + GpuPlacementPolicy.FormatLogFields(input, result)));

        string? usedName = GpuPlacementPolicy.UsedName(input.Device, input.Order);
        if (result.Reason == "software-adapter")
        {
            logs.Add(new(GpuPlacementLogLevel.Warning,
                $"GPU placement software adapter in use: used=\"{usedName}\"（GPU の指定では直りません。ドライバーを確かめてください）"));
        }

        string? warningText = null;
        string? warningToolTip = null;
        if (result.Verdict == GpuPlacementVerdict.IntegratedInUse && input.Device is { } used)
        {
            string highPerformanceName =
                GpuPlacementPolicy.AdapterName(input.Order, input.Order?.HighPerformanceLuid) ?? "名前不明";
            warningText = GpuPlacementPolicy.WarningText(used.Origin);
            warningToolTip = GpuPlacementPolicy.WarningToolTip(usedName ?? "名前不明", highPerformanceName, used.Origin);
            logs.Add(new(GpuPlacementLogLevel.Warning,
                $"GPU placement warning shown: at={(timing == GpuPlacementTiming.Startup ? "startup" : "recovery")} " +
                $"used=\"{usedName}\" highPerformance=\"{highPerformanceName}\" origin={GpuPlacementPolicy.OriginText(used.Origin)} reason={result.Reason}"));
        }

        return new(input, result, logs, warningText,
            usedName is null ? null : GpuPlacementPolicy.NameText(usedName), warningToolTip);
    }
}

/// <summary>
/// 画面の状態（設計書 5.1・5.3・5.4）。警告が見えている間は名前の行を隠す。「×」はその起動の間だけ消す。
/// 判定し直す（Apply）と消した印を戻すので、復旧の後に IntegratedInUse ならまた出る。
/// </summary>
internal sealed class GpuPlacementDisplay
{
    public string? WarningText { get; private set; }
    public string? WarningToolTip { get; private set; }
    public string? NameText { get; private set; }
    public bool Dismissed { get; private set; }

    public bool WarningVisible => WarningText is not null && !Dismissed;
    public bool NameVisible => NameText is not null && !WarningVisible;

    public void Apply(GpuPlacementReport report)
    {
        WarningText = report.WarningText;
        WarningToolTip = report.WarningToolTip;
        NameText = report.NameText;
        Dismissed = false;
    }

    public void Dismiss() => Dismissed = WarningText is not null;
}

/// <summary>DXGI の Factory を毎回作って一覧と 2 つの並びを読む（合成のデバイスには触らない）。</summary>
internal sealed class DxgiGpuAdapterOrderSource : IGpuAdapterOrderSource
{
    public GpuAdapterOrder Read()
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var adapters = new List<GpuAdapterInfo>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
        {
            using (adapter)
            {
                AdapterDescription1 description = adapter.Description1;
                adapters.Add(new(description.Luid, description.Description ?? "", description.VendorId,
                    (description.Flags & AdapterFlags.Software) != 0));
            }
        }

        using IDXGIFactory6? factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        if (factory6 is null)
            return new(adapters, null, null);
        return new(adapters,
            FirstByPreference(factory6, GpuPreference.HighPerformance),
            FirstByPreference(factory6, GpuPreference.MinimumPower));
    }

    private static long? FirstByPreference(IDXGIFactory6 factory, GpuPreference preference)
    {
        if (factory.EnumAdapterByGpuPreference(0, preference, out IDXGIAdapter1? adapter).Failure || adapter is null)
            return null;
        using (adapter)
            return adapter.Description1.Luid;
    }

    private const int SmRemoteSession = 0x1000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>リモートのセッションか（GetSystemMetrics(SM_REMOTESESSION)）。</summary>
    public static bool IsRemoteSession() => GetSystemMetrics(SmRemoteSession) != 0;
}

/// <summary>
/// 合成のデバイスを作った直後（GPU worker の起動と、復旧の作り直しの直後。合成の周期の外）に 1 回だけ呼ぶ。
/// 名前・ベンダー・UMA の読みが失敗しても null（ログでは ?）で続け、デバイス作りを失敗にしない。
/// </summary>
internal static class GpuDeviceIdentityReader
{
    public static GpuDeviceIdentity Read(GpuDevice device, GpuDeviceOrigin origin)
    {
        string? name = null;
        uint? vendor = null;
        bool? uma = null;
        try
        {
            AdapterDescription1 description = device.Adapter.Description1;
            name = description.Description;
            vendor = description.VendorId;
        }
        catch (Exception e)
        {
            Serilog.Log.Warning("GPU placement adapter read failed: {Type}: {Message}", e.GetType().Name, e.Message);
        }
        try
        {
            Vortice.Direct3D11.FeatureDataD3D11Options2 options = default;
            if (device.Device.CheckFeatureSupport(Vortice.Direct3D11.Feature.D3D11Options2, ref options))
                uma = options.UnifiedMemoryArchitecture;
        }
        catch (Exception e)
        {
            Serilog.Log.Warning("GPU placement uma read failed: {Type}: {Message}", e.GetType().Name, e.Message);
        }
        return new(device.Luid, origin, name, vendor, uma);
    }
}
