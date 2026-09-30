using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.0: 「ProRes の GPU 復号」の 3 択（起動時の選択・再起動の案内・保存の配線）。</summary>
public class ProResGpuUiTests
{
    private sealed class FakeLtcMonitor : ILtcMonitor
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

    [Theory]
    [InlineData("Auto", 0)]
    [InlineData("On", 1)]
    [InlineData("Off", 2)]
    public void Initialize_SelectsTheStartupValueWithoutRestartNotice(string modeName, int index)
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());
        ProResGpuMode mode = Enum.Parse<ProResGpuMode>(modeName);

        vm.InitializeProResGpu(mode);

        vm.ProResGpuModeIndex.Should().Be(index);
        vm.ProResGpuMode.Should().Be(mode);
        vm.ProResGpuRestartNotice.Should().BeEmpty();
    }

    [Fact]
    public void ChangingSelection_ShowsRestartNoticeOnlyWhileItDiffersFromStartup()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());
        vm.InitializeProResGpu(ProResGpuMode.Auto);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ProResGpuModeIndex = 2;

        vm.ProResGpuMode.Should().Be(ProResGpuMode.Off);
        vm.ProResGpuRestartNotice.Should().Be("再起動の後に反映");
        changed.Should().Contain(nameof(SyncViewModel.ProResGpuMode));
        changed.Should().Contain(nameof(SyncViewModel.ProResGpuRestartNotice));

        vm.ProResGpuModeIndex = 0;

        vm.ProResGpuRestartNotice.Should().BeEmpty("起動時の値に戻したら案内は消える");
    }

    [Fact]
    public void MainWindow_WiresTheComboAndSavesTheSetting()
    {
        string root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "TimecodeSyncPlayer"));
        string xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));

        xaml.Should().Contain("AutomationProperties.AutomationId=\"ProResGpuCombo\"");
        xaml.Should().Contain("SelectedIndex=\"{Binding Sync.ProResGpuModeIndex, Mode=TwoWay}\"");
        xaml.Should().Contain("Text=\"{Binding Sync.ProResGpuRestartNotice}\"");
        // 項目の並びは SyncViewModel の添字（0=auto, 1=on, 2=off）と同じ。
        Regex.Matches(xaml, "<ComboBoxItem Content=\"(自動|有効|無効)\" Tag=\"(\\w+)\"/>")
            .Select(m => m.Groups[2].Value)
            .Should().Equal("auto", "on", "off");
        codeBehind.Should().Contain("_vm.Sync.InitializeProResGpu(ProResGpuPolicy.Resolve(settingsManager.Current.ProResGpu));");
        codeBehind.Should().Contain("ProResGpu = value,");
    }
}
