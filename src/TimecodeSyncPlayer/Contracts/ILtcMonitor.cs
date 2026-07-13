namespace TimecodeSyncPlayer.Contracts;

public interface ILtcMonitor : IDisposable
{
    event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived;
    event EventHandler<Exception?>? Stopped;
    void Start(string? deviceName);
    void Stop();
    bool IsRunning { get; }
    string? DeviceName { get; }
    int SampleRate { get; }
    IReadOnlyList<string> GetCaptureDeviceNames();
}
