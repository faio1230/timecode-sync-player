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
}
