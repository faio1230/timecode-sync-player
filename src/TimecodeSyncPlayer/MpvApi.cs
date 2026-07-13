using System.Runtime.InteropServices;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal sealed class MpvApi : IMpvApi
{
    public IntPtr Create() => Mpv.mpv_create();
    public int Initialize(IntPtr ctx) => Mpv.mpv_initialize(ctx);
    public void TerminateDestroy(IntPtr ctx) => Mpv.mpv_terminate_destroy(ctx);
    public int SetPropertyString(IntPtr ctx, string name, string value) => Mpv.mpv_set_property_string(ctx, name, value);
    public int GetProperty(IntPtr ctx, string name, int format, out double result) => Mpv.mpv_get_property(ctx, name, format, out result);
    public string GetPropertyString(IntPtr ctx, string name) => Mpv.GetString(ctx, name);
    public int CommandString(IntPtr ctx, string args) => Mpv.mpv_command_string(ctx, args);
    public void Free(IntPtr data) => Mpv.mpv_free(data);
    public int FormatDouble => Mpv.FormatDouble;
}
