namespace TimecodeSyncPlayer;

public sealed class StartupBufferInitializer
{
    private readonly PixelBufferManager _bufferManager;

    public StartupBufferInitializer(PixelBufferManager bufferManager)
    {
        _bufferManager = bufferManager;
    }

    public void Initialize(string renderPixelFormat)
    {
        _bufferManager.InitFormatString(renderPixelFormat);
        _bufferManager.InitStridePtr();
    }
}
