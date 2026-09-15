using System.IO;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Tests.Integration;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T3: 全体に効く同期オフセット（SyncOffsetMs）。単位は ms、プラスで映像が先行する。
/// 適用は同期の入口（LTC 有効フレームの受け入れ）で 1 回だけ。
/// </summary>
public class SyncOffsetTests
{
    private sealed class NoopLtcMonitor : ILtcMonitor
    {
        public bool IsRunning => false;
        public string? DeviceName => null;
        public int SampleRate => 0;
        public void Start(string? deviceName) { }
        public void Stop() { }
        public IReadOnlyList<string> GetCaptureDeviceNames() => [];
        public event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public void Dispose() { }
    }

    // ---- 設定（ms 一貫・既定 0・clamp） ----

    [Fact]
    public void Default_IsZeroMilliseconds()
    {
        AppSettings.Default.SyncOffsetMs.Should().Be(0.0);
    }

    [Theory]
    [InlineData(-1000.5, -1000.0)]
    [InlineData(1000.5, 1000.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 0.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    public void ValidateSettings_ClampsOutOfRangeToRangeInMilliseconds(double value, double expected)
    {
        AppSettings settings = AppSettings.Default with { SyncOffsetMs = value };

        AppSettingsManager.ValidateSettings(settings).SyncOffsetMs.Should().Be(expected);
    }

    [Theory]
    [InlineData(-1000.0)]
    [InlineData(-37.5)]
    [InlineData(0.0)]
    [InlineData(80.0)]
    [InlineData(1000.0)]
    public void ValidateSettings_KeepsInRangeValueUnchanged(double value)
    {
        AppSettings settings = AppSettings.Default with { SyncOffsetMs = value };

        AppSettingsManager.ValidateSettings(settings).SyncOffsetMs.Should().Be(value);
    }

    [Fact]
    public void Serialization_UsesCamelCaseMillisecondsKeyAndRoundTrips()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        string json = JsonSerializer.Serialize(AppSettings.Default with { SyncOffsetMs = 80 }, options);

        json.Should().Contain("\"syncOffsetMs\": 80");
        JsonSerializer.Deserialize<AppSettings>(json, options)!.SyncOffsetMs.Should().Be(80);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsSyncOffsetMs()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tsp-sync-offset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await new AppSettingsManager(path).SaveAsync(AppSettings.Default with { SyncOffsetMs = -42.5 });

            var reader = new AppSettingsManager(path);
            await reader.LoadAsync();

            reader.Current.SyncOffsetMs.Should().Be(-42.5);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- UI 表示（ms のみ・フレーム換算なし） ----

    [Fact]
    public void FormatMilliseconds_ShowsUnitAndSignConvention()
    {
        SyncOffsetPolicy.FormatMilliseconds(0).Should().Be("0 ms");
        SyncOffsetPolicy.FormatMilliseconds(80).Should().Be("+80 ms");
        SyncOffsetPolicy.FormatMilliseconds(-33.5).Should().Be("-33.5 ms");
        SyncOffsetPolicy.FormatMilliseconds(80).Should().NotContain("フレーム");
        SyncOffsetPolicy.FormatMilliseconds(80).Should().NotContainEquivalentOf("frame");
    }

    [Fact]
    public void ViewModel_ExposesValueAndTextInMilliseconds()
    {
        var vm = new SyncViewModel(new NoopLtcMonitor());

        vm.SyncOffsetMs = 80;

        vm.SyncOffsetMs.Should().Be(80);
        vm.SyncOffsetText.Should().Be("+80 ms");
    }

    [Fact]
    public void ViewModel_ClampsOutOfRangeToMillisecondsRange()
    {
        var vm = new SyncViewModel(new NoopLtcMonitor());

        vm.SyncOffsetMs = 5000;
        vm.SyncOffsetMs.Should().Be(1000);

        vm.SyncOffsetMs = -5000;
        vm.SyncOffsetMs.Should().Be(-1000);
        vm.SyncOffsetText.Should().Be("-1000 ms");
    }

    // ---- 適用（同期の入口で 1 回） ----

    [Theory]
    [InlineData(0.0, 3.0)]
    [InlineData(100.0, 3.1)]
    [InlineData(-100.0, 2.9)]
    public void SingleMode_OffsetShiftsSeekTargetByTheSameAmount(double offsetMs, double expectedTarget)
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("track", 0);
        h.ChangeMode(SyncMode.Single);
        h.SyncOffsetMilliseconds = offsetMs;
        h.Operations.Clear();

        h.SupplyLtc(3.0);

        h.Operations.Should().ContainSingle(o => o.Name == "seek")
            .Which.Value.Should().BeApproximately(expectedTarget, 1e-9);
    }

    [Theory]
    [InlineData(0.0, 3.0)]
    [InlineData(100.0, 3.1)]
    [InlineData(-100.0, 2.9)]
    public void ContinueMode_OffsetShiftsSeekTargetByTheSameAmount(double offsetMs, double expectedTarget)
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("track", 0);
        h.ReloadProject();
        h.ManualPlay();
        h.SyncOffsetMilliseconds = offsetMs;
        h.Operations.Clear();

        h.SupplyLtc(3.0);

        h.Operations.Should().ContainSingle(o => o.Name == "seek")
            .Which.Value.Should().BeApproximately(expectedTarget, 1e-9);
    }

    [Fact]
    public void ContinueMode_OffsetShiftsClipSwitchTiming()
    {
        var withoutOffset = new SyncScenarioHarness();
        withoutOffset.AddTrack("first", 0, 5);
        withoutOffset.AddTrack("next", 5, 5);
        withoutOffset.ReloadProject();
        withoutOffset.ManualPlay();
        withoutOffset.Operations.Clear();

        var withOffset = new SyncScenarioHarness();
        withOffset.AddTrack("first", 0, 5);
        withOffset.AddTrack("next", 5, 5);
        withOffset.ReloadProject();
        withOffset.ManualPlay();
        withOffset.SyncOffsetMilliseconds = 100;
        withOffset.Operations.Clear();

        withoutOffset.SupplyLtc(4.95);
        withOffset.SupplyLtc(4.95);

        withoutOffset.Operations.Should().NotContain(o => o.Name == "loadfile");
        withoutOffset.Operations.Should().Contain(o => o.Name == "seek");
        withOffset.Operations.Should().Contain(o => o.Name == "loadfile");
    }

    [Fact]
    public void Offset_DoesNotChangeTheDisplayedLtcValue()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("track", 0);
        h.ReloadProject();
        h.ManualPlay();
        h.SyncOffsetMilliseconds = 250;
        h.Operations.Clear();

        h.SupplyLtc(3.0);

        h.Controller.LastLtcSeconds.Should().Be(3.0);
        h.Operations.Should().ContainSingle(o => o.Name == "seek")
            .Which.Value.Should().BeApproximately(3.25, 1e-9);
    }

    // ---- ポリシー（純関数） ----

    [Fact]
    public void Apply_AddsOffsetSecondsOnce()
    {
        SyncOffsetPolicy.Apply(3.0, 0.0).Should().Be(3.0);
        SyncOffsetPolicy.Apply(3.0, 100.0).Should().BeApproximately(3.1, 1e-12);
        SyncOffsetPolicy.Apply(3.0, -100.0).Should().BeApproximately(2.9, 1e-12);
        SyncOffsetPolicy.Apply(3.0, 5000.0).Should().BeApproximately(4.0, 1e-12);
    }

    // ---- UI 配線（ms 表記・AutomationId・同期入口への供給） ----

    [Fact]
    public void MainWindow_WiresOffsetUiInMillisecondsAndFeedsTheSyncEntry()
    {
        string root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "TimecodeSyncPlayer"));
        string xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"SyncOffsetSlider\"");
        xaml.Should().Contain("Value=\"{Binding Sync.SyncOffsetMs, Mode=TwoWay}\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"SyncOffsetValueText\"");
        xaml.Should().Contain("Text=\"{Binding Sync.SyncOffsetText}\"");
        xaml.Should().Contain("＋で映像が先行");
        codeBehind.Should().Contain("GetSyncOffsetMilliseconds: () => _vm.Sync.SyncOffsetMs");
        codeBehind.Should().Contain("SyncOffsetMs = _vm.Sync.SyncOffsetMs,");
    }
}
