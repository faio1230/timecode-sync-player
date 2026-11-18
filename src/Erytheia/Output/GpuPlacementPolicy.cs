using System.Globalization;
using System.Text;

namespace Erytheia.Output;

/// <summary>v0.6.8: GPU の置き場の判定の結果（設計書 docs/design/v0.6.8-gpu-warning.md の 4.2）。</summary>
internal enum GpuPlacementVerdict { Ok, IntegratedInUse, NotJudged }

/// <summary>判定の時点。起動（WaitForDevice の後）と、復旧の完了（Recovered）の後の 2 つだけ。</summary>
internal enum GpuPlacementTiming { Startup, Recovery }

/// <summary>合成のデバイスの選ばれ方。</summary>
internal enum GpuDeviceOrigin
{
    /// <summary>保存された全画面の出力先を列挙したアダプター。</summary>
    SavedDisplay,
    /// <summary>DXGI の既定のアダプター（出力先が保存されていない、または見つからない）。</summary>
    Default,
    /// <summary>復旧で同じアダプターに作れず、既定のアダプターへ移った。</summary>
    RecoveryDefault,
}

/// <summary>DXGI のアダプターの一覧の 1 件。</summary>
internal sealed record GpuAdapterInfo(long Luid, string Name, uint VendorId, bool IsSoftware);

/// <summary>アダプターの一覧と、Windows の分類による 2 つの並びの先頭（IDXGIFactory6 が無ければ null）。</summary>
internal sealed record GpuAdapterOrder(
    IReadOnlyList<GpuAdapterInfo> Adapters,
    long? HighPerformanceLuid,
    long? MinimumPowerLuid);

/// <summary>
/// 合成のデバイスを作った場所（GPU worker の起動と、復旧の作り直しの直後）で 1 回だけ読む値。
/// UI はこれを読むだけで、合成の D3D11 デバイスには触らない（復旧でデバイスが捨てられるため）。
/// 読めなかった値は null（ログでは ?）。
/// </summary>
internal sealed record GpuDeviceIdentity(long Luid, GpuDeviceOrigin Origin, string? Name, uint? VendorId, bool? Uma);

internal sealed record GpuPlacementInput(
    GpuPlacementTiming Timing,
    bool PlaybackAvailable,
    bool DeviceReady,
    GpuDeviceIdentity? Device,
    GpuAdapterOrder? Order,
    bool RemoteSession,
    bool Forced);

internal sealed record GpuPlacementResult(GpuPlacementVerdict Verdict, string Reason);

/// <summary>
/// v0.6.8: 「独立の GPU があるのに、Windows が内蔵（省電力の並びの先頭）と分類した GPU で動いている」かの判定（B 案）。
/// 純粋な関数。材料の取得は <see cref="GpuPlacementJudge"/> が行う。ベンダーの名前とメモリの量は使わない。
/// UMA（UnifiedMemoryArchitecture）は記録だけで、判定には使わない（検証機の値を見てから決める）。
/// </summary>
internal static class GpuPlacementPolicy
{
    /// <summary>E2E・検証用の注入。判定の結果を IntegratedInUse に固定する。設定項目にもインストーラーにも出さない。</summary>
    internal const string ForceWarningEnvironmentVariable = "ERYTHEIA_FORCE_GPU_WARNING";

    internal const string LogPrefix = "GPU placement: ";

    public static GpuPlacementResult Evaluate(GpuPlacementInput input)
    {
        // 規則 0: 再生が無効、またはデバイスの準備が時間切れ。
        if (!input.PlaybackAvailable)
            return NotJudged("playback-unavailable");
        if (!input.DeviceReady || input.Device is null)
            return NotJudged("device-timeout");
        if (input.Forced)
            return new(GpuPlacementVerdict.IntegratedInUse, "forced");

        GpuAdapterOrder? order = input.Order;
        // 規則 1: 一覧が取れない、または IDXGIFactory6 が無い。
        if (order is null || order.HighPerformanceLuid is not long highPerformance || order.MinimumPowerLuid is not long minimumPower)
            return NotJudged("no-preference-order");
        // 規則 2: リモートのセッション（一覧が変わることがある）。
        if (input.RemoteSession)
            return NotJudged("remote-session");

        long used = input.Device.Luid;
        // 規則 3: ソフトウェアのアダプターで動いている（GPU の指定では直らない）。
        if (order.Adapters.Any(adapter => adapter.Luid == used && adapter.IsSoftware))
            return NotJudged("software-adapter");
        // 規則 4: ソフトウェアのアダプター（Basic Render Driver など）を除いて 1 つ以下。
        if (HardwareAdapterCount(order) <= 1)
            return new(GpuPlacementVerdict.Ok, "single-hardware-adapter");
        // 規則 5: Windows の分類で差が無い。
        if (highPerformance == minimumPower)
            return new(GpuPlacementVerdict.Ok, "same-preference-head");
        // 規則 6: 省電力の並びの先頭で動いていて、高性能の先頭と違う。
        if (used == minimumPower)
            return new(GpuPlacementVerdict.IntegratedInUse, "minimum-power-head-in-use");
        // 規則 7。
        return new(GpuPlacementVerdict.Ok, "not-minimum-power-head");
    }

    public static GpuPlacementResult Failed() => NotJudged("error");

    private static GpuPlacementResult NotJudged(string reason) => new(GpuPlacementVerdict.NotJudged, reason);

    internal static int HardwareAdapterCount(GpuAdapterOrder order) =>
        order.Adapters.Where(adapter => !adapter.IsSoftware).Select(adapter => adapter.Luid).Distinct().Count();

    /// <summary>変数が無い・空・0・false・no のときは効かない（ERYTHEIA_FORCE_GPU_UNAVAILABLE と同じ読み方）。</summary>
    internal static bool IsForced(string? value) => OutputBackendState.IsForcedUnavailable(value);

    /// <summary>名前は DXGI_ADAPTER_DESC1.Description をそのまま。空なら「名前不明」と VendorId の 16 進。</summary>
    public static string DisplayName(string? name, uint? vendorId)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();
        return vendorId is uint vendor ? $"名前不明 {FormatVendor(vendor)}" : "名前不明";
    }

    /// <summary>いま使っている GPU の名前。デバイスから読めた名前を先に使い、無ければ一覧から引く。</summary>
    public static string? UsedName(GpuDeviceIdentity? device, GpuAdapterOrder? order)
    {
        if (device is null)
            return null;
        GpuAdapterInfo? listed = Find(order, device.Luid);
        string? name = !string.IsNullOrWhiteSpace(device.Name) ? device.Name : listed?.Name;
        return DisplayName(name, device.VendorId ?? listed?.VendorId);
    }

    public static string? AdapterName(GpuAdapterOrder? order, long? luid) =>
        luid is long value && Find(order, value) is { } adapter ? DisplayName(adapter.Name, adapter.VendorId) : null;

    private static GpuAdapterInfo? Find(GpuAdapterOrder? order, long luid) =>
        order?.Adapters.FirstOrDefault(adapter => adapter.Luid == luid);

    /// <summary>
    /// 画面の警告の枠の文面（設計書 5.2・12.7）。選ばれ方（origin）を受け取り、その場合に効く直し方を 1 つだけ出す。
    /// 今は default と saved-display が同じ文。saved-display でも、ハイブリッドの機体では指定で出力を並べるアダプターが
    /// 変わりうる（11-16 の 1 回の観測）。最終は検証機の回で分け直すので、場合ごとの枝は残す。
    /// GPU の名前と 4K60 の話はツールチップ（<see cref="WarningToolTip"/>）へ。文面はこの 1 か所（と WarningToolTip）にまとめる。
    /// </summary>
    public static string WarningText(GpuDeviceOrigin origin) => origin switch
    {
        GpuDeviceOrigin.SavedDisplay => HighPerformancePreferenceText,
        GpuDeviceOrigin.RecoveryDefault =>
            "⚠ GPU の復旧で内蔵 GPU に移りました。区切りのよいところで再起動してください。",
        _ => HighPerformancePreferenceText,
    };

    private const string HighPerformancePreferenceText =
        "⚠ 内蔵 GPU で動いています。Windows の「設定 → ディスプレイ → グラフィック」で ERYTHEIA.exe を" +
        "「高パフォーマンス」にして、再起動してください。";

    /// <summary>警告の枠の文に付けるツールチップ。2 つの GPU の名前、4K60 の話、もう一方の直し方。</summary>
    public static string WarningToolTip(string usedName, string highPerformanceName, GpuDeviceOrigin origin) =>
        $"いま使っている GPU: {usedName}\n高性能の GPU: {highPerformanceName}\n4K60 の素材はカクつくことがあります。\n" +
        origin switch
        {
            GpuDeviceOrigin.SavedDisplay =>
                "それでも消えないときは、ディスプレイを高性能の GPU 側の端子につなぐか、全画面の出力先を見直してください。",
            GpuDeviceOrigin.RecoveryDefault =>
                "再起動すると、全画面の出力先か Windows の指定に従って GPU を選び直します。",
            _ =>
                "全画面の出力先が内蔵 GPU の側のディスプレイのときは、出力先も見直してください。",
        };

    /// <summary>常時表示の名前の行（設計書 5.4）。</summary>
    public static string NameText(string usedName) => "GPU: " + usedName;

    /// <summary>6 節の Information の行（`GPU placement: ` の後ろ）。取れなかった値は ?。</summary>
    public static string FormatLogFields(GpuPlacementInput input, GpuPlacementResult result)
    {
        GpuDeviceIdentity? device = input.Device;
        GpuAdapterOrder? order = input.Order;
        GpuAdapterInfo? listed = device is null ? null : Find(order, device.Luid);
        uint? vendor = device?.VendorId ?? listed?.VendorId;
        var fields = new StringBuilder();
        fields.Append("at=").Append(input.Timing == GpuPlacementTiming.Startup ? "startup" : "recovery");
        fields.Append(" used=").Append(Quoted(UsedName(device, order)));
        fields.Append(" vendor=").Append(vendor is uint v ? FormatVendor(v) : "?");
        fields.Append(" luid=").Append(device is null ? "?" : device.Luid.ToString(CultureInfo.InvariantCulture));
        fields.Append(" origin=").Append(device is null ? "?" : OriginText(device.Origin));
        fields.Append(" highPerformance=").Append(Quoted(AdapterName(order, order?.HighPerformanceLuid)));
        fields.Append(" minimumPower=").Append(Quoted(AdapterName(order, order?.MinimumPowerLuid)));
        fields.Append(" hardwareAdapters=").Append(order is null ? "?" : HardwareAdapterCount(order).ToString(CultureInfo.InvariantCulture));
        fields.Append(" uma=").Append(device?.Uma is bool uma ? (uma ? "1" : "0") : "?");
        fields.Append(" remote=").Append(input.RemoteSession ? "1" : "0");
        fields.Append(" verdict=").Append(result.Verdict);
        fields.Append(" reason=").Append(result.Reason);
        return fields.ToString();
    }

    public static string OriginText(GpuDeviceOrigin origin) => origin switch
    {
        GpuDeviceOrigin.SavedDisplay => "saved-display",
        GpuDeviceOrigin.RecoveryDefault => "recovery-default",
        _ => "default",
    };

    /// <summary>起動のデバイスの選ばれ方。LUID を渡したなら保存された出力先（作れなければ GpuDevice が例外）。</summary>
    public static GpuDeviceOrigin StartupOrigin(long? requestedLuid) =>
        requestedLuid is null ? GpuDeviceOrigin.Default : GpuDeviceOrigin.SavedDisplay;

    /// <summary>
    /// 復旧で作り直したデバイスの選ばれ方。作った後の LUID と、求めた LUID を比べるだけで決める
    /// （CreateGpuDeviceForRecovery の分岐には手を入れない）。既定へ移った先が求めた LUID と同じアダプターなら、
    /// 使う GPU は同じなので saved-display と出す。
    /// </summary>
    public static GpuDeviceOrigin RecoveryOrigin(long? requestedLuid, long actualLuid) => requestedLuid switch
    {
        null => GpuDeviceOrigin.Default,
        long requested when requested == actualLuid => GpuDeviceOrigin.SavedDisplay,
        _ => GpuDeviceOrigin.RecoveryDefault,
    };

    /// <summary>判定し直すのは Recovered の後だけ。Failed では名前と警告を最後の判定のまま残す（設計書 4.4）。</summary>
    public static bool ShouldReevaluate(GpuOutputStatus status) => status == GpuOutputStatus.Recovered;

    private static string FormatVendor(uint vendor) => "0x" + vendor.ToString("X4", CultureInfo.InvariantCulture);

    private static string Quoted(string? value) => value is null ? "?" : "\"" + value.Replace('"', '\'') + "\"";
}
