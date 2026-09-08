using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer;

// Windows SDK 10.0.26100.0 um/d3d11.h: ID3D11DeviceVtbl / ID3D11DeviceContextVtbl.
// Zero-based COM slots: CreateQuery 24, End 28, GetData 29, Flush 111, IUnknown.Release 2.
// D3D11_QUERY_DESC is 8 bytes; EVENT=0. GetData uses a 4-byte BOOL and DONOTFLUSH=1.
internal sealed class SpoutGpuCompletion : ISpoutGpuCompletion
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryDesc { public uint Query; public uint MiscFlags; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateQueryDelegate(IntPtr self, ref QueryDesc desc, out IntPtr query);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EndDelegate(IntPtr self, IntPtr query);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void FlushDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetDataDelegate(IntPtr self, IntPtr query, out int completed, uint size, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate uint ReleaseDelegate(IntPtr self);

    private readonly IntPtr _context; // Borrowed; sender must outlive this object.
    private readonly EndDelegate _end;
    private readonly FlushDelegate _flush;
    private readonly GetDataDelegate _getData;
    private ReleaseDelegate? _release;
    private IntPtr _query; // Only this COM reference is owned.

    internal SpoutGpuCompletion(IntPtr device, IntPtr context)
    {
        if (device == IntPtr.Zero || context == IntPtr.Zero)
            throw new InvalidOperationException("Spout D3D11 device/context is unavailable.");
        _context = context;
        _end = Method<EndDelegate>(context, 28);
        _flush = Method<FlushDelegate>(context, 111);
        _getData = Method<GetDataDelegate>(context, 29);
        var create = Method<CreateQueryDelegate>(device, 24);
        var desc = new QueryDesc();
        int hr = create(device, ref desc, out _query);
        try
        {
            if (_query != IntPtr.Zero) _release = Method<ReleaseDelegate>(_query, 2);
            if (hr < 0) throw new COMException($"Spout CreateQuery failed (HRESULT 0x{hr:X8}).", hr);
            if (_query == IntPtr.Zero) throw new InvalidOperationException("Spout CreateQuery returned no query.");
        }
        catch { Dispose(); throw; }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    public void End() => _end(_context, _query);
    public void Flush() => _flush(_context);
    public int GetData(out int completed) => _getData(_context, _query, out completed, 4, 1);
    public void Dispose()
    {
        IntPtr query = _query; _query = IntPtr.Zero;
        if (query != IntPtr.Zero) _release!(query);
    }
}
