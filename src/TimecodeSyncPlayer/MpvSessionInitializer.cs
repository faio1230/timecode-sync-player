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

    public MpvSessionInitializationResult Initialize(bool showDebugOsd = false)
    {
        IntPtr mpv = _mpvApi.Create();
        if (mpv == IntPtr.Zero)
            return new MpvSessionInitializationResult(false, IntPtr.Zero, MpvSessionInitializationFailure.CreateFailed);

        _startupPropertyApplier.Apply(mpv, showDebugOsd);

        int rc = _mpvApi.Initialize(mpv);
        if (rc < 0)
        {
            _mpvApi.TerminateDestroy(mpv);
            return new MpvSessionInitializationResult(
                false,
                IntPtr.Zero,
                MpvSessionInitializationFailure.InitializeFailed);
        }

        return new MpvSessionInitializationResult(true, mpv, MpvSessionInitializationFailure.None);
    }
}
