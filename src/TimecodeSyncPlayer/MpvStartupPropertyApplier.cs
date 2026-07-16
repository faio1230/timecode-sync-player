using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

public sealed class MpvStartupPropertyApplier
{
    private readonly IMpvApi _mpvApi;

    public MpvStartupPropertyApplier(IMpvApi mpvApi)
    {
        _mpvApi = mpvApi;
    }

    public void Apply(IntPtr mpv, bool showDebugOsd = false)
    {
        _mpvApi.SetPropertyString(mpv, "vo", "libmpv");
        _mpvApi.SetPropertyString(mpv, "hwdec", "auto-copy");
        _mpvApi.SetPropertyString(mpv, "keep-open", "always");
        _mpvApi.SetPropertyString(mpv, "pause", "yes");
        _mpvApi.SetPropertyString(mpv, "osd-level", showDebugOsd ? "3" : "1");
        _mpvApi.SetPropertyString(mpv, "osd-font-size", "20");
        _mpvApi.SetPropertyString(mpv, "osd-bar", "yes");
        _mpvApi.SetPropertyString(mpv, "osd-color", "#FFFFFF");
        _mpvApi.SetPropertyString(mpv, "osd-border-color", "#000000");
        _mpvApi.SetPropertyString(mpv, "osd-border-size", "2");
    }
}
