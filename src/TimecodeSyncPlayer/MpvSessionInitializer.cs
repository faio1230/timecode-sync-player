using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

public enum MpvSessionInitializationFailure
{
    None,
    CreateFailed,
    InitializeFailed
}

public sealed record MpvSessionInitializationResult(
    bool Success,
    IntPtr Mpv,
    MpvSessionInitializationFailure Failure);

public sealed class MpvSessionInitializer
{
    private readonly IMpvApi _mpvApi;
    private readonly MpvStartupPropertyApplier _startupPropertyApplier;

    public MpvSessionInitializer(IMpvApi mpvApi, MpvStartupPropertyApplier startupPropertyApplier)
    {
        _mpvApi = mpvApi;
        _startupPropertyApplier = startupPropertyApplier;
    }

    public MpvSessionInitializationResult Initialize()
    {
        IntPtr mpv = _mpvApi.Create();
        if (mpv == IntPtr.Zero)
            return new MpvSessionInitializationResult(false, IntPtr.Zero, MpvSessionInitializationFailure.CreateFailed);

        _startupPropertyApplier.Apply(mpv);

        int rc = _mpvApi.Initialize(mpv);
        if (rc < 0)
            return new MpvSessionInitializationResult(false, mpv, MpvSessionInitializationFailure.InitializeFailed);

        return new MpvSessionInitializationResult(true, mpv, MpvSessionInitializationFailure.None);
    }
}
