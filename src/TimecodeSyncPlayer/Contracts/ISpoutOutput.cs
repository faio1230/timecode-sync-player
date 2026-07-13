namespace TimecodeSyncPlayer;

public interface ISpoutOutput : IDisposable
{
    bool IsEnabled { get; set; }
    bool IsAvailable { get; }
    bool TryInitialize();
    void SendFrame(IntPtr pixels, int width, int height);
}
