using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Contracts;

public interface IMpvApi
{
    IntPtr Create();
    int Initialize(IntPtr ctx);
    void TerminateDestroy(IntPtr ctx);
    int SetPropertyString(IntPtr ctx, string name, string value);
    int GetProperty(IntPtr ctx, string name, int format, out double result);
    string GetPropertyString(IntPtr ctx, string name);
    int CommandString(IntPtr ctx, string args);
    void Free(IntPtr data);
    int FormatDouble { get; }
}
