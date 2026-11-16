using FluentAssertions;
using Erytheia.Output;

namespace Erytheia.Tests;

/// <summary>
/// v0.6.8: GPU の置き場の判定（設計書 docs/design/v0.6.8-gpu-warning.md の 4.3 の場合の表と 7.1）。
/// アダプターの一覧と並びは注入する。
/// </summary>
public class GpuPlacementPolicyTests
{
    private const long Rtx = 101, Amd = 202, Basic = 303, Rtx2 = 404, Egpu = 505;
    private static readonly GpuAdapterInfo RtxAdapter = new(Rtx, "NVIDIA GeForce RTX 4070 Laptop GPU", 0x10DE, false);
    private static readonly GpuAdapterInfo AmdAdapter = new(Amd, "AMD Radeon(TM) Graphics", 0x1002, false);
    private static readonly GpuAdapterInfo BasicAdapter = new(Basic, "Microsoft Basic Render Driver", 0x1414, true);
    private static readonly GpuAdapterInfo Rtx2Adapter = new(Rtx2, "NVIDIA RTX A2000", 0x10DE, false);
    private static readonly GpuAdapterInfo EgpuAdapter = new(Egpu, "NVIDIA GeForce RTX 3080 (eGPU)", 0x10DE, false);

    private static GpuDeviceIdentity Device(long luid, GpuDeviceOrigin origin = GpuDeviceOrigin.Default) =>
        new(luid, origin, null, null, null);

    private static GpuPlacementInput Input(
        long used,
        GpuAdapterOrder? order,
        bool remote = false,
        bool forced = false,
        bool playbackAvailable = true,
        bool deviceReady = true,
        GpuPlacementTiming timing = GpuPlacementTiming.Startup) =>
        new(timing, playbackAvailable, deviceReady, Device(used), order, remote, forced);

    private static GpuAdapterOrder Order(long? highPerformance, long? minimumPower, params GpuAdapterInfo[] adapters) =>
        new(adapters, highPerformance, minimumPower);

    // ── 4.3 の場合の表 ─────────────────────────────────────────────

    private static readonly Dictionary<string, (GpuPlacementInput Input, GpuPlacementVerdict Verdict, string Reason)> Cases = new()
    {
        ["GPU が 1 つだけ（デスクトップの RTX）"] = (Input(Rtx, Order(Rtx, Rtx, RtxAdapter)), GpuPlacementVerdict.Ok, "single-hardware-adapter"),
        ["Basic Render Driver が一覧にある GPU 1 つの機体"] = (Input(Rtx, Order(Rtx, Rtx, RtxAdapter, BasicAdapter)), GpuPlacementVerdict.Ok, "single-hardware-adapter"),
        ["Basic Render Driver が並びの先頭に出ても GPU 1 つなら Ok"] = (Input(Rtx, Order(Rtx, Basic, RtxAdapter, BasicAdapter)), GpuPlacementVerdict.Ok, "single-hardware-adapter"),
        ["独立 2 つ、並びの中の順が同じ"] = (Input(Rtx2, Order(Rtx, Rtx, RtxAdapter, Rtx2Adapter)), GpuPlacementVerdict.Ok, "same-preference-head"),
        ["独立 2 つ、順が違い、高性能の先頭を使用"] = (Input(Rtx, Order(Rtx, Rtx2, RtxAdapter, Rtx2Adapter)), GpuPlacementVerdict.Ok, "not-minimum-power-head"),
        // B だけでは誤発火しうる場合（4.1・4.3）。B かつ C（UMA）にするかは検証機の値を見てから決める。
        ["独立 2 つ、順が違い、省電力の先頭を使用（B だけの既知の制限）"] = (Input(Rtx2, Order(Rtx, Rtx2, RtxAdapter, Rtx2Adapter)), GpuPlacementVerdict.IntegratedInUse, "minimum-power-head-in-use"),
        ["内蔵＋独立で、独立を使用（平常のノート PC）"] = (Input(Rtx, Order(Rtx, Amd, AmdAdapter, RtxAdapter)), GpuPlacementVerdict.Ok, "not-minimum-power-head"),
        ["内蔵＋独立で、内蔵を使用"] = (Input(Amd, Order(Rtx, Amd, AmdAdapter, RtxAdapter, BasicAdapter)), GpuPlacementVerdict.IntegratedInUse, "minimum-power-head-in-use"),
        ["外付けの箱の GPU＋内蔵で、内蔵を使用"] = (Input(Amd, Order(Egpu, Amd, AmdAdapter, EgpuAdapter)), GpuPlacementVerdict.IntegratedInUse, "minimum-power-head-in-use"),
        ["外付けの箱の GPU＋内蔵で、箱を使用"] = (Input(Egpu, Order(Egpu, Amd, AmdAdapter, EgpuAdapter)), GpuPlacementVerdict.Ok, "not-minimum-power-head"),
        ["リモート デスクトップ"] = (Input(Amd, Order(Rtx, Amd, AmdAdapter, RtxAdapter), remote: true), GpuPlacementVerdict.NotJudged, "remote-session"),
        ["ソフトウェアのアダプターで動いている"] = (Input(Basic, Order(Rtx, Amd, AmdAdapter, RtxAdapter, BasicAdapter)), GpuPlacementVerdict.NotJudged, "software-adapter"),
        ["IDXGIFactory6 が無い"] = (Input(Amd, Order(null, null, AmdAdapter, RtxAdapter)), GpuPlacementVerdict.NotJudged, "no-preference-order"),
        ["一覧が取れない"] = (Input(Amd, null), GpuPlacementVerdict.NotJudged, "no-preference-order"),
        ["再生が無効"] = (Input(Amd, Order(Rtx, Amd, AmdAdapter, RtxAdapter), playbackAvailable: false), GpuPlacementVerdict.NotJudged, "playback-unavailable"),
        ["デバイスの準備が時間切れ"] = (Input(Amd, Order(Rtx, Amd, AmdAdapter, RtxAdapter), deviceReady: false), GpuPlacementVerdict.NotJudged, "device-timeout"),
        ["復旧で内蔵へ移った"] = (Input(Amd, Order(Rtx, Amd, AmdAdapter, RtxAdapter), timing: GpuPlacementTiming.Recovery), GpuPlacementVerdict.IntegratedInUse, "minimum-power-head-in-use"),
    };

    public static TheoryData<string> CaseTable()
    {
        var data = new TheoryData<string>();
        foreach (string name in Cases.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseTable))]
    public void Evaluate_FollowsCaseTable(string name)
    {
        (GpuPlacementInput input, GpuPlacementVerdict verdict, string reason) = Cases[name];
        GpuPlacementResult result = GpuPlacementPolicy.Evaluate(input);

        result.Verdict.Should().Be(verdict, name);
        result.Reason.Should().Be(reason, name);
    }

    [Fact]
    public void Evaluate_UmaIsRecordedOnly_DoesNotChangeVerdict()
    {
        GpuAdapterOrder order = Order(Rtx, Amd, AmdAdapter, RtxAdapter);
        foreach (bool? uma in new bool?[] { null, false, true })
        {
            var input = new GpuPlacementInput(GpuPlacementTiming.Startup, true, true,
                new GpuDeviceIdentity(Rtx, GpuDeviceOrigin.Default, null, null, uma), order, false, false);
            GpuPlacementPolicy.Evaluate(input).Verdict.Should().Be(GpuPlacementVerdict.Ok);
        }
    }

    // ── 試験のための切り替え（7.3） ─────────────────────────────────

    [Fact]
    public void Evaluate_Forced_IsIntegratedInUseEvenOnSingleGpu()
    {
        GpuPlacementResult result = GpuPlacementPolicy.Evaluate(Input(Rtx, Order(Rtx, Rtx, RtxAdapter), forced: true));

        result.Should().Be(new GpuPlacementResult(GpuPlacementVerdict.IntegratedInUse, "forced"));
    }

    [Fact]
    public void Evaluate_Forced_DoesNotOverridePlaybackUnavailable()
    {
        GpuPlacementPolicy.Evaluate(Input(Rtx, Order(Rtx, Rtx, RtxAdapter), forced: true, playbackAvailable: false))
            .Reason.Should().Be("playback-unavailable");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public void IsForced_OnlyWhenSetToTruthyValue(string? value, bool expected)
    {
        GpuPlacementPolicy.IsForced(value).Should().Be(expected);
    }

    [Fact]
    public void Run_WithoutForce_ReadsSameVerdictAsPolicy()
    {
        GpuAdapterOrder order = Order(Rtx, Rtx, RtxAdapter);
        foreach (string? value in new string?[] { null, "", "0" })
        {
            GpuPlacementReport report = Run(order, Device(Rtx), forced: GpuPlacementPolicy.IsForced(value));
            report.Result.Verdict.Should().Be(GpuPlacementVerdict.Ok);
            report.WarningText.Should().BeNull();
            report.Logs.Should().NotContain(entry => entry.Text.Contains(GpuPlacementPolicy.ForceWarningEnvironmentVariable));
        }
    }

    // ── 文面と名前 ───────────────────────────────────────────────

    [Fact]
    public void WarningText_GivesOneFixMatchingTheOrigin()
    {
        GpuPlacementPolicy.WarningText(GpuDeviceOrigin.Default).Should().Be(
            "⚠ 内蔵 GPU で動いています。Windows の「設定 → ディスプレイ → グラフィック」で ERYTHEIA.exe を「高パフォーマンス」にして、再起動してください。");
        // saved-display も、今は default と同じ文（検証機で指定の後に RTX で動いた 1 回の観測。設計書 12.7）。
        GpuPlacementPolicy.WarningText(GpuDeviceOrigin.SavedDisplay)
            .Should().Be(GpuPlacementPolicy.WarningText(GpuDeviceOrigin.Default));
        GpuPlacementPolicy.WarningText(GpuDeviceOrigin.RecoveryDefault).Should().Be(
            "⚠ GPU の復旧で内蔵 GPU に移りました。区切りのよいところで再起動してください。");
        GpuPlacementPolicy.WarningText(GpuDeviceOrigin.RecoveryDefault)
            .Should().NotBe(GpuPlacementPolicy.WarningText(GpuDeviceOrigin.Default));
    }

    /// <summary>全角を 2、半角を 1 と数えた幅（画面の折り返しの目安）。</summary>
    private static int DisplayWidth(string text) => text.Sum(c => c <= 0x7F ? 1 : c == '⚠' ? 1 : 2);

    [Fact]
    public void WarningText_NoneIsLongerThanTheDefaultMeasuredInE2E()
    {
        // 開発機の E2E（選ばれ方は default）で枠の高さと一覧の高さを測った。ほかの文面がそれより長いと、測った数を超える。
        int measured = DisplayWidth(GpuPlacementPolicy.WarningText(GpuDeviceOrigin.Default));
        foreach (GpuDeviceOrigin origin in Enum.GetValues<GpuDeviceOrigin>())
            DisplayWidth(GpuPlacementPolicy.WarningText(origin)).Should().BeLessThanOrEqualTo(measured, origin.ToString());
    }

    [Fact]
    public void WarningToolTip_HasNamesStutterNoteAndTheOtherFix()
    {
        string tip = GpuPlacementPolicy.WarningToolTip("AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 4070 Laptop GPU", GpuDeviceOrigin.Default);
        tip.Should().StartWith("いま使っている GPU: AMD Radeon(TM) Graphics\n高性能の GPU: NVIDIA GeForce RTX 4070 Laptop GPU\n4K60 の素材はカクつくことがあります。\n");
        tip.Should().EndWith("全画面の出力先が内蔵 GPU の側のディスプレイのときは、出力先も見直してください。");

        GpuPlacementPolicy.WarningToolTip("A", "B", GpuDeviceOrigin.SavedDisplay)
            .Should().EndWith("それでも消えないときは、ディスプレイを高性能の GPU 側の端子につなぐか、全画面の出力先を見直してください。")
            .And.Contain("端子").And.Contain("全画面の出力先を見直して");
        GpuPlacementPolicy.WarningToolTip("A", "B", GpuDeviceOrigin.RecoveryDefault)
            .Should().Contain("4K60 の素材はカクつくことがあります。");
    }

    [Fact]
    public void Run_UsesTheDeviceOriginForTheText_AndForceDoesNotChooseTheOrigin()
    {
        GpuAdapterOrder order = Order(Rtx, Amd, AmdAdapter, RtxAdapter);
        foreach (GpuDeviceOrigin origin in Enum.GetValues<GpuDeviceOrigin>())
        {
            Run(order, Device(Amd, origin)).WarningText.Should().Be(GpuPlacementPolicy.WarningText(origin));
        }
        // 注入はデバイスの選ばれ方をそのまま使う（選ばれ方を指定する口は無い）。
        Run(Order(Rtx, Rtx, RtxAdapter), Device(Rtx, GpuDeviceOrigin.Default), forced: true)
            .WarningText.Should().Be(GpuPlacementPolicy.WarningText(GpuDeviceOrigin.Default));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070", 0x10DEu, "NVIDIA GeForce RTX 3070")]
    [InlineData("", 0x1002u, "名前不明 0x1002")]
    [InlineData("   ", 0x8086u, "名前不明 0x8086")]
    [InlineData(null, null, "名前不明")]
    public void DisplayName_FallsBackToVendorId(string? name, uint? vendor, string expected)
    {
        GpuPlacementPolicy.DisplayName(name, vendor).Should().Be(expected);
    }

    [Fact]
    public void Run_EmptyAdapterName_ShowsVendorIdInWarningAndName()
    {
        var nameless = new GpuAdapterInfo(Amd, "", 0x1002, false);
        GpuPlacementReport report = Run(Order(Rtx, Amd, nameless, RtxAdapter), Device(Amd));

        report.WarningToolTip.Should().StartWith("いま使っている GPU: 名前不明 0x1002\n");
        report.NameText.Should().Be("GPU: 名前不明 0x1002");
    }

    [Fact]
    public void UsedName_PrefersNameReadFromDevice()
    {
        var device = new GpuDeviceIdentity(Rtx, GpuDeviceOrigin.Default, "From device", 0x10DE, null);
        GpuPlacementPolicy.UsedName(device, Order(Rtx, Rtx, RtxAdapter)).Should().Be("From device");
        GpuPlacementPolicy.UsedName(Device(Rtx), Order(Rtx, Rtx, RtxAdapter)).Should().Be(RtxAdapter.Name);
    }

    // ── ログの行 ─────────────────────────────────────────────────

    [Fact]
    public void FormatLogFields_HasAllFieldsInOrder()
    {
        var device = new GpuDeviceIdentity(Amd, GpuDeviceOrigin.SavedDisplay, "AMD Radeon(TM) Graphics", 0x1002, true);
        var input = new GpuPlacementInput(GpuPlacementTiming.Startup, true, true, device,
            Order(Rtx, Amd, AmdAdapter, RtxAdapter, BasicAdapter), false, false);

        string line = GpuPlacementPolicy.FormatLogFields(input, GpuPlacementPolicy.Evaluate(input));

        line.Should().Be(
            "at=startup used=\"AMD Radeon(TM) Graphics\" vendor=0x1002 luid=202 origin=saved-display " +
            "highPerformance=\"NVIDIA GeForce RTX 4070 Laptop GPU\" minimumPower=\"AMD Radeon(TM) Graphics\" " +
            "hardwareAdapters=2 uma=1 remote=0 verdict=IntegratedInUse reason=minimum-power-head-in-use");
    }

    [Fact]
    public void FormatLogFields_UnknownValuesAreQuestionMarks()
    {
        var input = new GpuPlacementInput(GpuPlacementTiming.Startup, false, false, null, null, false, false);

        GpuPlacementPolicy.FormatLogFields(input, GpuPlacementPolicy.Evaluate(input)).Should().Be(
            "at=startup used=? vendor=? luid=? origin=? highPerformance=? minimumPower=? " +
            "hardwareAdapters=? uma=? remote=0 verdict=NotJudged reason=playback-unavailable");
    }

    [Fact]
    public void FormatLogFields_QuoteInNameDoesNotBreakTheLine()
    {
        var odd = new GpuAdapterInfo(Rtx, "GPU \"X\"", 0x10DE, false);
        var input = Input(Rtx, Order(Rtx, Rtx, odd));

        GpuPlacementPolicy.FormatLogFields(input, GpuPlacementPolicy.Evaluate(input))
            .Should().Contain("used=\"GPU 'X'\"");
    }

    // ── 材料の集めと失敗の扱い（4.4） ─────────────────────────────────

    private sealed class FixedOrderSource(Func<GpuAdapterOrder> read) : IGpuAdapterOrderSource
    {
        public int Reads { get; private set; }
        public GpuAdapterOrder Read() { Reads++; return read(); }
    }

    private static GpuPlacementReport Run(
        GpuAdapterOrder order,
        GpuDeviceIdentity? device,
        bool forced = false,
        GpuPlacementTiming timing = GpuPlacementTiming.Startup) =>
        GpuPlacementJudge.Run(timing, true, true, () => device, new FixedOrderSource(() => order), () => false, forced);

    private static IEnumerable<string> Lines(GpuPlacementReport report, GpuPlacementLogLevel level) =>
        report.Logs.Where(entry => entry.Level == level).Select(entry => entry.Text);

    [Fact]
    public void Run_SingleGpu_OneInformationLineAndNoWarning()
    {
        GpuPlacementReport report = Run(Order(Rtx, Rtx, RtxAdapter, BasicAdapter), Device(Rtx));

        report.Logs.Should().ContainSingle();
        Lines(report, GpuPlacementLogLevel.Information).Single()
            .Should().StartWith("GPU placement: at=startup used=\"NVIDIA GeForce RTX 4070 Laptop GPU\"");
        report.WarningText.Should().BeNull();
        report.NameText.Should().Be("GPU: NVIDIA GeForce RTX 4070 Laptop GPU");
    }

    [Fact]
    public void Run_IntegratedInUse_WritesWarningShownLine()
    {
        GpuPlacementReport report = Run(Order(Rtx, Amd, AmdAdapter, RtxAdapter), Device(Amd));

        report.Result.Verdict.Should().Be(GpuPlacementVerdict.IntegratedInUse);
        Lines(report, GpuPlacementLogLevel.Warning).Should().ContainSingle().Which.Should().Be(
            "GPU placement warning shown: at=startup used=\"AMD Radeon(TM) Graphics\" " +
            "highPerformance=\"NVIDIA GeForce RTX 4070 Laptop GPU\" origin=default reason=minimum-power-head-in-use");
        report.WarningText.Should().Be(GpuPlacementPolicy.WarningText(GpuDeviceOrigin.Default));
        report.WarningToolTip.Should().Contain("いま使っている GPU: AMD Radeon(TM) Graphics");
    }

    [Fact]
    public void Run_Forced_WritesInjectionWarningAndForcedReason()
    {
        GpuPlacementReport report = Run(Order(Rtx, Rtx, RtxAdapter), Device(Rtx), forced: true);

        Lines(report, GpuPlacementLogLevel.Warning).Should().Contain(line =>
            line.StartsWith("検証用の注入: ERYTHEIA_FORCE_GPU_WARNING により"));
        Lines(report, GpuPlacementLogLevel.Information).Single().Should().EndWith("verdict=IntegratedInUse reason=forced");
        Lines(report, GpuPlacementLogLevel.Warning).Should().Contain(line => line.StartsWith("GPU placement warning shown: "));
        report.WarningText.Should().StartWith("⚠ 内蔵 GPU で動いています。");
        report.WarningToolTip.Should().Contain("いま使っている GPU: NVIDIA GeForce RTX 4070 Laptop GPU");
    }

    [Fact]
    public void Run_SoftwareAdapter_AddsWarningLineButNoScreenWarning()
    {
        GpuPlacementReport report = Run(Order(Rtx, Amd, AmdAdapter, RtxAdapter, BasicAdapter), Device(Basic));

        report.Result.Reason.Should().Be("software-adapter");
        Lines(report, GpuPlacementLogLevel.Warning).Should().ContainSingle()
            .Which.Should().StartWith("GPU placement software adapter in use: used=\"Microsoft Basic Render Driver\"");
        report.WarningText.Should().BeNull();
    }

    public static TheoryData<string> FailurePoints => new() { "device", "order", "remote" };

    [Theory]
    [MemberData(nameof(FailurePoints))]
    public void Run_MaterialThrows_IsNotJudgedErrorWithoutWarningAndDoesNotThrow(string failAt)
    {
        GpuAdapterOrder order = Order(Rtx, Amd, AmdAdapter, RtxAdapter);
        Func<GpuPlacementReport> act = () => GpuPlacementJudge.Run(
            GpuPlacementTiming.Startup, true, true,
            () => failAt == "device" ? throw new InvalidOperationException("device read") : Device(Amd),
            new FixedOrderSource(() => failAt == "order" ? throw new System.Runtime.InteropServices.COMException("enum") : order),
            () => failAt == "remote" ? throw new InvalidOperationException("remote") : false,
            forced: false);

        GpuPlacementReport report = act.Should().NotThrow().Subject;

        report.Result.Should().Be(new GpuPlacementResult(GpuPlacementVerdict.NotJudged, "error"));
        report.WarningText.Should().BeNull();
        Lines(report, GpuPlacementLogLevel.Warning).Should().ContainSingle()
            .Which.Should().StartWith("GPU placement failed: ");
        Lines(report, GpuPlacementLogLevel.Information).Single().Should().EndWith("verdict=NotJudged reason=error");
    }

    /// <summary>許した回数を超えて列挙すると例外を投げる一覧（Evaluate の 2 回の後、行の組み立てで投げる）。</summary>
    private sealed class ThrowingAfterList(IReadOnlyList<GpuAdapterInfo> inner, int allowed) : IReadOnlyList<GpuAdapterInfo>
    {
        private int enumerations;
        public GpuAdapterInfo this[int index] => inner[index];
        public int Count => inner.Count;
        public IEnumerator<GpuAdapterInfo> GetEnumerator() =>
            ++enumerations > allowed ? throw new InvalidOperationException("late enumeration") : inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Run_ThrowsWhileBuildingText_IsNotJudgedErrorAndDoesNotThrow()
    {
        var adapters = new ThrowingAfterList([AmdAdapter, RtxAdapter], allowed: 2);
        GpuAdapterOrder order = new(adapters, Rtx, Amd);
        Func<GpuPlacementReport> act = () => Run(order, Device(Amd));

        GpuPlacementReport report = act.Should().NotThrow().Subject;

        report.Result.Should().Be(new GpuPlacementResult(GpuPlacementVerdict.NotJudged, "error"));
        report.WarningText.Should().BeNull();
        report.NameText.Should().BeNull();
        Lines(report, GpuPlacementLogLevel.Warning).Should().ContainSingle()
            .Which.Should().Be("GPU placement failed: InvalidOperationException: late enumeration");
        Lines(report, GpuPlacementLogLevel.Information).Single().Should().EndWith("verdict=NotJudged reason=error");
    }

    [Fact]
    public void Run_NullEmptyAndUnlistedNames_DoNotThrow()
    {
        var empty = new GpuAdapterInfo(Amd, "", 0x1002, false);
        foreach (GpuDeviceIdentity device in new[]
                 {
                     new GpuDeviceIdentity(Amd, GpuDeviceOrigin.Default, null, null, null),
                     new GpuDeviceIdentity(Amd, GpuDeviceOrigin.Default, "", null, null),
                     new GpuDeviceIdentity(999, GpuDeviceOrigin.RecoveryDefault, null, null, null),
                 })
        {
            foreach (GpuAdapterOrder order in new[]
                     {
                         Order(Rtx, Amd, empty, RtxAdapter),
                         Order(Rtx, 888, empty, RtxAdapter),
                         Order(777, Amd, empty, RtxAdapter),
                         Order(null, null),
                     })
            {
                GpuPlacementReport report = Run(order, device);
                report.Result.Reason.Should().NotBe("error");
                report.Logs.Should().Contain(entry => entry.Text.StartsWith(GpuPlacementPolicy.LogPrefix));
            }
        }
    }

    [Fact]
    public void Run_OrderThrows_NameStillShownFromDevice()
    {
        var device = new GpuDeviceIdentity(Amd, GpuDeviceOrigin.Default, "AMD Radeon(TM) Graphics", 0x1002, null);
        GpuPlacementReport report = GpuPlacementJudge.Run(GpuPlacementTiming.Startup, true, true, () => device,
            new FixedOrderSource(() => throw new InvalidOperationException("enum")), () => false, forced: false);

        report.NameText.Should().Be("GPU: AMD Radeon(TM) Graphics");
    }

    [Fact]
    public void Run_PlaybackUnavailable_WritesLineWithoutReadingMaterials()
    {
        var source = new FixedOrderSource(() => throw new InvalidOperationException("must not read"));
        GpuPlacementReport report = GpuPlacementJudge.Run(GpuPlacementTiming.Startup, false, false,
            () => throw new InvalidOperationException("must not read"), source, () => throw new InvalidOperationException("must not read"),
            forced: true);

        source.Reads.Should().Be(0);
        report.Logs.Should().ContainSingle().Which.Should().Be(new GpuPlacementLogEntry(GpuPlacementLogLevel.Information,
            "GPU placement: at=startup used=? vendor=? luid=? origin=? highPerformance=? minimumPower=? " +
            "hardwareAdapters=? uma=? remote=0 verdict=NotJudged reason=playback-unavailable"));
        report.NameText.Should().BeNull();
        report.WarningText.Should().BeNull();
    }

    [Fact]
    public void Run_DeviceTimeout_IsNotJudged()
    {
        GpuPlacementReport report = GpuPlacementJudge.Run(GpuPlacementTiming.Startup, true, false,
            () => null, new FixedOrderSource(() => Order(Rtx, Rtx, RtxAdapter)), () => false, forced: false);

        report.Result.Reason.Should().Be("device-timeout");
        report.Logs.Should().ContainSingle();
    }

    // ── 選ばれ方 ─────────────────────────────────────────────────

    [Fact]
    public void Origins_StartupAndRecovery()
    {
        GpuPlacementPolicy.StartupOrigin(null).Should().Be(GpuDeviceOrigin.Default);
        GpuPlacementPolicy.StartupOrigin(Rtx).Should().Be(GpuDeviceOrigin.SavedDisplay);
        GpuPlacementPolicy.RecoveryOrigin(null, Amd).Should().Be(GpuDeviceOrigin.Default);
        GpuPlacementPolicy.RecoveryOrigin(Rtx, Rtx).Should().Be(GpuDeviceOrigin.SavedDisplay);
        GpuPlacementPolicy.RecoveryOrigin(Rtx, Amd).Should().Be(GpuDeviceOrigin.RecoveryDefault);
    }

    // ── 復旧と表示（4.4・5.3） ─────────────────────────────────────

    [Fact]
    public void Recovery_ToOtherAdapter_UpdatesNameAndReshowsDismissedWarning()
    {
        GpuAdapterOrder order = Order(Rtx, Amd, AmdAdapter, RtxAdapter);
        var display = new GpuPlacementDisplay();

        // 起動は内蔵で警告。×で消す。
        display.Apply(Run(order, Device(Amd)));
        display.WarningVisible.Should().BeTrue();
        display.NameVisible.Should().BeFalse("警告の 1 文目に同じ名前があるので名前の行は隠す");
        display.Dismiss();
        display.WarningVisible.Should().BeFalse();
        display.NameVisible.Should().BeTrue();
        display.NameText.Should().Be("GPU: AMD Radeon(TM) Graphics");

        // 復旧で RTX へ移る。名前が変わり、警告は無い。
        GpuPlacementReport toRtx = Run(order, new GpuDeviceIdentity(Rtx, GpuDeviceOrigin.RecoveryDefault, null, null, null),
            timing: GpuPlacementTiming.Recovery);
        display.Apply(toRtx);
        display.WarningVisible.Should().BeFalse();
        display.NameText.Should().Be("GPU: NVIDIA GeForce RTX 4070 Laptop GPU");
        Lines(toRtx, GpuPlacementLogLevel.Information).Single().Should().StartWith("GPU placement: at=recovery ")
            .And.Contain(" origin=recovery-default ");

        // ×で消していても、復旧の後に IntegratedInUse ならまた出す。
        display.Apply(Run(order, Device(Rtx)));
        display.Dismiss();
        display.Apply(Run(order, new GpuDeviceIdentity(Amd, GpuDeviceOrigin.RecoveryDefault, null, null, null),
            timing: GpuPlacementTiming.Recovery));
        display.WarningVisible.Should().BeTrue();
    }

    [Fact]
    public void Display_DismissWithoutWarning_KeepsName()
    {
        var display = new GpuPlacementDisplay();
        display.Apply(Run(Order(Rtx, Rtx, RtxAdapter), Device(Rtx)));

        display.Dismiss();

        display.Dismissed.Should().BeFalse();
        display.NameVisible.Should().BeTrue();
    }

    [Theory]
    [InlineData("Recovering,Recovered", 1)]
    [InlineData("Recovering,Failed", 0)]
    [InlineData("Recovering,Failed,Recovering,Recovered", 1)]
    [InlineData("Recovering,Recovered,Recovering,Recovered", 2)]
    public void ShouldReevaluate_OnlyAfterRecovered(string sequence, int expected)
    {
        sequence.Split(',').Select(Enum.Parse<GpuOutputStatus>)
            .Count(GpuPlacementPolicy.ShouldReevaluate).Should().Be(expected);
    }
}
