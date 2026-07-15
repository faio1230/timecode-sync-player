using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

public class SyncViewModelTests
{
    private sealed class FakeLtcMonitor : ILtcMonitor
    {
        public bool IsRunning { get; private set; }
        public string? DeviceName { get; private set; }
        public int SampleRate => 0;
        public void Start(string? deviceName) { DeviceName = deviceName; IsRunning = true; }
        public void Stop() { IsRunning = false; }
        public IReadOnlyList<string> GetCaptureDeviceNames() => [];
        public event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public void Dispose() { }
    }

    [Fact]
    public void StartLtcCommand_StartsMonitor()
    {
        var monitor = new FakeLtcMonitor();
        var vm = new SyncViewModel(monitor);
        vm.SelectedDevice = "mic";

        vm.StartLtcCommand.Execute(null);

        monitor.IsRunning.Should().BeTrue();
        monitor.DeviceName.Should().Be("mic");
    }

    [Fact]
    public void StartLtcCommand_CanExecute_TrueInitially()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());

        vm.StartLtcCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void StopLtcCommand_StopsMonitor()
    {
        var monitor = new FakeLtcMonitor();
        var vm = new SyncViewModel(monitor);
        vm.StartLtcCommand.Execute(null); // IsLtcRunning = true

        vm.StopLtcCommand.Execute(null);

        monitor.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void StartLtcCommand_CanExecute_FalseWhenRunning()
    {
        var monitor = new FakeLtcMonitor();
        var vm = new SyncViewModel(monitor);
        vm.StartLtcCommand.Execute(null);

        vm.StartLtcCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void StopLtcCommand_CanExecute_TrueWhenRunning()
    {
        var monitor = new FakeLtcMonitor();
        var vm = new SyncViewModel(monitor);
        vm.StartLtcCommand.Execute(null);

        vm.StopLtcCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ToggleSyncCommand_TogglesIsEnabled()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());

        vm.ToggleSyncCommand.Execute(null);
        vm.SyncEnabled.Should().BeTrue();

        vm.ToggleSyncCommand.Execute(null);
        vm.SyncEnabled.Should().BeFalse();
    }

    [Fact]
    public void SyncModeIndex_PropertyChangedFires()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());
        var changedProps = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        vm.SyncModeIndex = 1;

        changedProps.Should().Contain(nameof(vm.SyncModeIndex));
    }

    [Fact]
    public void SyncModeIndex_MapsToSyncMode()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());

        vm.SyncModeIndex = 1;
        vm.SyncMode.Should().Be(SyncMode.Continue);

        vm.SyncModeIndex = 0;
        vm.SyncMode.Should().Be(SyncMode.Single);
    }

    [Fact]
    public void GapBehaviorIndex_MapsToGapBehavior()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());

        vm.GapBehaviorIndex = 1;
        vm.GapBehavior.Should().Be(GapBehavior.Freeze);

        vm.GapBehaviorIndex = 0;
        vm.GapBehavior.Should().Be(GapBehavior.Black);
    }

    [Fact]
    public void IsContinueMode_TrueWhenSyncModeIndexIsOne()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());

        vm.SyncModeIndex = 1;
        vm.IsContinueMode.Should().BeTrue();

        vm.SyncModeIndex = 0;
        vm.IsContinueMode.Should().BeFalse();
    }

    [Fact]
    public void StartLtcCommand_FiresStartLtcFailedOnException()
    {
        var monitor = new ThrowingLtcMonitor();
        var vm = new SyncViewModel(monitor);
        Exception? captured = null;
        vm.StartLtcFailed += (_, ex) => captured = ex;

        vm.StartLtcCommand.Execute(null);

        captured.Should().NotBeNull();
        vm.IsLtcRunning.Should().BeFalse();
    }

    [Fact]
    public void StopLtcCommand_FiresStopLtcFailedAndClearsRunningOnException()
    {
        var vm = new SyncViewModel(new ThrowingStopLtcMonitor()) { IsLtcRunning = true };
        Exception? captured = null;
        vm.StopLtcFailed += (_, ex) => captured = ex;

        vm.StopLtcCommand.Execute(null);

        captured.Should().BeOfType<InvalidOperationException>();
        vm.IsLtcRunning.Should().BeFalse();
    }

    [Fact]
    public void ToggleSyncCommand_RaisesChangedEventWithNewValue()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());
        var values = new List<bool>();
        vm.SyncEnabledChanged += (_, enabled) => values.Add(enabled);

        vm.ToggleSyncCommand.Execute(null);
        vm.ToggleSyncCommand.Execute(null);

        values.Should().Equal(true, false);
        vm.SyncToggleLabel.Should().Be("Sync OFF");
    }

    [Theory]
    [InlineData(0, TimecodeFpsMode.Auto)]
    [InlineData(1, TimecodeFpsMode.Fixed24)]
    [InlineData(2, TimecodeFpsMode.Fixed25)]
    [InlineData(3, TimecodeFpsMode.Fixed29_97)]
    [InlineData(4, TimecodeFpsMode.Fixed30)]
    [InlineData(-1, TimecodeFpsMode.Auto)]
    [InlineData(5, TimecodeFpsMode.Auto)]
    public void LtcFpsModeIndex_MapsKnownAndOutOfRangeValues(
        int index,
        TimecodeFpsMode expected)
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());

        vm.LtcFpsModeIndex = index;

        vm.LtcFpsMode.Should().Be(expected);
    }

    [Fact]
    public void DisplayProperties_RaisePropertyChangedAndReturnAssignedValues()
    {
        var vm = new SyncViewModel(new FakeLtcMonitor());
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.SelectedDevice = "line-in";
        vm.SyncEnabled = true;
        vm.GapBehaviorIndex = 1;
        vm.LtcTimecodeText = "01:02:03:04";
        vm.LtcRealTimeText = "3723.160 s";
        vm.LtcFormatText = "fps: 25";
        vm.SpoutToggleLabel = "Spout ON";
        vm.TimelineToggleLabel = "Timeline ON";

        vm.SelectedDevice.Should().Be("line-in");
        vm.SyncEnabled.Should().BeTrue();
        vm.SyncToggleLabel.Should().Be("Sync ON");
        vm.GapBehavior.Should().Be(GapBehavior.Freeze);
        vm.LtcTimecodeText.Should().Be("01:02:03:04");
        vm.LtcRealTimeText.Should().Be("3723.160 s");
        vm.LtcFormatText.Should().Be("fps: 25");
        vm.SpoutToggleLabel.Should().Be("Spout ON");
        vm.TimelineToggleLabel.Should().Be("Timeline ON");
        changedProperties.Should().Contain([
            nameof(vm.SelectedDevice),
            nameof(vm.SyncEnabled),
            nameof(vm.SyncToggleLabel),
            nameof(vm.GapBehaviorIndex),
            nameof(vm.GapBehavior),
            nameof(vm.LtcTimecodeText),
            nameof(vm.LtcRealTimeText),
            nameof(vm.LtcFormatText),
            nameof(vm.SpoutToggleLabel),
            nameof(vm.TimelineToggleLabel)
        ]);
    }

    private sealed class ThrowingLtcMonitor : ILtcMonitor
    {
        public bool IsRunning => false;
        public string? DeviceName => null;
        public int SampleRate => 0;
        public void Start(string? deviceName) => throw new InvalidOperationException("device error");
        public void Stop() { }
        public IReadOnlyList<string> GetCaptureDeviceNames() => [];
        public event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class ThrowingStopLtcMonitor : ILtcMonitor
    {
        public bool IsRunning => true;
        public string? DeviceName => null;
        public int SampleRate => 0;
        public void Start(string? deviceName) { }
        public void Stop() => throw new InvalidOperationException("stop error");
        public IReadOnlyList<string> GetCaptureDeviceNames() => [];
        public event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public void Dispose() { }
    }
}
