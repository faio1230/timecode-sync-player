using System.Runtime.InteropServices;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// SpoutDX.dll の spoutDX C++ クラスへの P/Invoke バインド。
/// x64 MSVC ABI では this ポインタは第1引数（RCX）として渡されるため、
/// C++ メンバー関数はマングル名を EntryPoint に指定することで直接バインドできる。
/// </summary>
internal static class SpoutNative
{
    private const string Dll = "SpoutDX.dll";

    // オブジェクトサイズ: spoutDX は spoutDirectX, spoutFrameCount,
    // spoutSenderNames, spoutCopy を継承する複合クラス。
    // 4096 バイトは余裕を持った安全サイズ（実測: sizeof(spoutDX) で検証すること）。
    private const int ObjectSize = 4096;

    private static bool _sizeValidated;

    // x64 MSVC ABI ではメンバー関数の呼び出し規約は ThisCall と等価。
    // CallingConvention.ThisCall を明示することで意図を明確にする。

    // コンストラクタ: void spoutDX::spoutDX()
    // this ポインタ（第1引数）に zeroed メモリを渡してインプレース初期化する。
    [DllImport(Dll, EntryPoint = "??0spoutDX@@QEAA@XZ",
        CallingConvention = CallingConvention.ThisCall)]
    internal static extern void Ctor(IntPtr self);

    // デストラクタ: void spoutDX::~spoutDX()
    [DllImport(Dll, EntryPoint = "??1spoutDX@@QEAA@XZ",
        CallingConvention = CallingConvention.ThisCall)]
    internal static extern void Dtor(IntPtr self);

    // bool spoutDX::OpenDirectX11(ID3D11Device* pDevice)
    // pDevice = IntPtr.Zero で SpoutDX が自前の D3D11 デバイスを作成する。
    [DllImport(Dll, EntryPoint = "?OpenDirectX11@spoutDX@@QEAA_NPEAUID3D11Device@@@Z",
        CallingConvention = CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool OpenDirectX11(IntPtr self, IntPtr pDevice);

    // bool spoutDX::SetSenderName(const char* name)
    [DllImport(Dll, EntryPoint = "?SetSenderName@spoutDX@@QEAA_NPEBD@Z",
        CallingConvention = CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SetSenderName(
        IntPtr self,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // bool spoutDX::SendImage(const BYTE* pData, UINT width, UINT height, UINT pitch)
    // pitch: 1行あたりのバイト数。bgr0/BGRA の tightly packed buffer では width * 4。
    [DllImport(Dll, EntryPoint = "?SendImage@spoutDX@@QEAA_NPEBEIII@Z",
        CallingConvention = CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SendImage(
        IntPtr self, IntPtr pData, uint width, uint height, uint pitch);

    // void spoutDX::ReleaseSender()
    [DllImport(Dll, EntryPoint = "?ReleaseSender@spoutDX@@QEAAXXZ",
        CallingConvention = CallingConvention.ThisCall)]
    internal static extern void ReleaseSender(IntPtr self);

    /// <summary>ObjectSize バイトの zeroed メモリを確保してコンストラクタを呼ぶ。</summary>
    internal static IntPtr Create()
    {
        IntPtr obj = Marshal.AllocHGlobal(ObjectSize);
        // コンストラクタ呼び出し前にゼロ初期化する
        for (int i = 0; i < ObjectSize; i += 8)
            Marshal.WriteInt64(obj, i, 0L);
        Ctor(obj);
        return obj;
    }

    /// <summary>デストラクタを呼んでメモリを解放する。</summary>
    internal static void Destroy(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        Dtor(obj);
        Marshal.FreeHGlobal(obj);
    }

    internal static void ValidateObjectSize()
    {
        if (_sizeValidated) return;

        try
        {
            var obj = Create();
            if (obj == IntPtr.Zero)
            {
                Log.Error("SpoutNative: Failed to create SpoutDX object");
                return;
            }

            SpoutNative.OpenDirectX11(obj, IntPtr.Zero);
            ReleaseSender(obj);
            Destroy(obj);

            _sizeValidated = true;
            Log.Information("SpoutNative: Object size validation passed (size={Size})", ObjectSize);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SpoutNative: Object size validation failed. ObjectSize={Size} may be insufficient", ObjectSize);
        }
    }
}
