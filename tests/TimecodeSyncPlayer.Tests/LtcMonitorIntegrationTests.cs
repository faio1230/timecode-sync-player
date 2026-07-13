using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class LtcMonitorIntegrationTests
{
    private class TestLtcMonitor : ILtcMonitor
    {
        public event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived;
        public event EventHandler<Exception?>? Stopped;
        public bool IsRunning { get; private set; }
        public string? DeviceName { get; private set; }
        public int SampleRate => 48000;
        public bool IsDisposed { get; private set; }

        public void Dispose() { IsDisposed = true; Stop(); }

        public IReadOnlyList<string> GetCaptureDeviceNames() => new[] { "Test Device" };

        public void Start(string? deviceName)
        {
            DeviceName = deviceName;
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void SimulateFrameReceived(LtcTimecode timecode, double fps)
        {
            double realTimeSeconds = timecode.ToRealSeconds(fps);
            FrameReceived?.Invoke(this, new LtcFrameReceivedEventArgs(timecode, fps, realTimeSeconds));
        }

        public void SimulateStopped(Exception? exception = null)
        {
            IsRunning = false;
            Stopped?.Invoke(this, exception);
        }
    }

    [Fact]
    public void LtcMonitor_Start_SetsIsRunning()
    {
        var monitor = new TestLtcMonitor();
        monitor.Start("Test Device");
        monitor.IsRunning.Should().BeTrue();
        monitor.DeviceName.Should().Be("Test Device");
    }

    [Fact]
    public void LtcMonitor_Stop_SetsIsRunningFalse()
    {
        var monitor = new TestLtcMonitor();
        monitor.Start("Test Device");
        monitor.Stop();
        monitor.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void LtcMonitor_Dispose_CallsStopAndMarksDisposed()
    {
        var monitor = new TestLtcMonitor();
        monitor.Dispose();
        monitor.IsDisposed.Should().BeTrue();
        monitor.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void LtcMonitor_FrameReceived_FiresWithCorrectData()
    {
        var monitor = new TestLtcMonitor();
        LtcFrameReceivedEventArgs? received = null;
        monitor.FrameReceived += (_, args) => received = args;

        var timecode = new LtcTimecode(1, 2, 3, 4, DropFrame: false);
        monitor.SimulateFrameReceived(timecode, 30.0);

        received.Should().NotBeNull();
        received!.Timecode.Should().Be(timecode);
        received!.Fps.Should().Be(30.0);
        received!.RealTimeSeconds.Should().BeApproximately(3723.133, 0.001);
    }

    [Fact]
    public void LtcMonitor_Stopped_FiresWithException()
    {
        var monitor = new TestLtcMonitor();
        Exception? receivedException = null;
        monitor.Stopped += (_, ex) => receivedException = ex;

        var testEx = new InvalidOperationException("Test error");
        monitor.SimulateStopped(testEx);

        receivedException.Should().BeSameAs(testEx);
    }

    [Fact]
    public void LtcMonitor_Stopped_FiresWithNullWhenNoError()
    {
        var monitor = new TestLtcMonitor();
        Exception? receivedException = new Exception("should be overwritten");
        monitor.Stopped += (_, ex) => receivedException = ex;

        monitor.SimulateStopped(null);

        receivedException.Should().BeNull();
    }

    [Fact]
    public void LtcMonitor_GetCaptureDeviceNames_ReturnsDevices()
    {
        var monitor = new TestLtcMonitor();
        var devices = monitor.GetCaptureDeviceNames();
        devices.Should().ContainSingle().Which.Should().Be("Test Device");
    }

    [Fact]
    public void LtcMonitor_SampleRate_ReturnsCorrectValue()
    {
        var monitor = new TestLtcMonitor();
        monitor.SampleRate.Should().Be(48000);
    }

    [Fact]
    public void LtcMonitor_MultipleStartCalls_StopsPrevious()
    {
        var monitor = new TestLtcMonitor();
        monitor.Start("Device1");
        monitor.Start("Device2");
        monitor.DeviceName.Should().Be("Device2");
        monitor.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void LtcFrameReceivedEventArgs_RealTimeSeconds_CalculatesCorrectly()
    {
        var tc = new LtcTimecode(0, 0, 1, 0, DropFrame: false);
        double seconds = tc.ToRealSeconds(25.0);
        seconds.Should().BeApproximately(1.0, 0.001);

        tc = new LtcTimecode(1, 0, 0, 0, DropFrame: false);
        seconds = tc.ToRealSeconds(30.0);
        seconds.Should().BeApproximately(3600.0, 0.001);
    }
}
