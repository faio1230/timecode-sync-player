namespace TimecodeSyncPlayer;

internal sealed class WindowLoadedUiInitializer
{
    private readonly Action _bindPlaylist;
    private readonly Action _subscribeLtc;
    private readonly Action _refreshLtcDevices;
    private readonly Action _applyAutoOffset;

    public WindowLoadedUiInitializer(
        Action bindPlaylist,
        Action subscribeLtc,
        Action refreshLtcDevices,
        Action applyAutoOffset)
    {
        _bindPlaylist = bindPlaylist;
        _subscribeLtc = subscribeLtc;
        _refreshLtcDevices = refreshLtcDevices;
        _applyAutoOffset = applyAutoOffset;
    }

    public void Initialize()
    {
        _bindPlaylist();
        _subscribeLtc();
        _refreshLtcDevices();
        _applyAutoOffset();
    }
}
