using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace TimecodeSyncPlayer.Output;

// None: 非共有。KeyedMutex: legacy DXGI ハンドル + keyed mutex。FenceNt: keyed mutex 無しの NT ハンドル + 共有フェンス。
internal enum SourceSharing { None, KeyedMutex, FenceNt }

/// <summary>
/// 合成側の D3D11 デバイス／コンテキスト／アダプター／EVENT query を所有する。試作 GpuDevice を移植。
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    public readonly IDXGIFactory2 Factory;
    public readonly IDXGIAdapter1 Adapter;
    public readonly ID3D11Device Device;
    public readonly ID3D11DeviceContext Context;
    public readonly GpuFence Fence;
    private readonly ID3D11Device1? device1;
    private readonly ID3D11Device5? device5;
    private readonly ID3D11DeviceContext4? context4;
    public long Luid { get; }

    public GpuDevice(long? luid, Action<string> fault, bool fenceSync = false)
    {
        using var build = new ConstructionScope();
        Factory = build.Add(CreateDXGIFactory1<IDXGIFactory2>());
        IDXGIAdapter1? selected = null;
        if (luid is { } value) selected = build.Add(Displays.FindAdapter(Factory, value));
        // Adapter 指定時は Unknown（アダプターのドライバー種別）、既定アダプターは Hardware を使う。
        var created = D3D11CreateDevice(selected, selected is null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out Device, out Context);
        if (Device != null) build.Add(Device);
        if (Context != null) build.Add(Context);
        created.CheckError();
        if (Device == null || Context == null) throw new InvalidOperationException("D3D11 creation returned no device/context.");
        // S3-2.3: immediate context は free-threaded ではない。GStreamer shim は Adopt したこのデバイスを
        // 別スレッドから使うため、Multithread 保護をここで明示的に有効化する（shim 任せにしない）。
        using (var multithread = Device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            if (multithread != null) multithread.SetMultithreadProtected(true);
        }
        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        using var actual = dxgi.GetAdapter();
        Luid = actual.Description.Luid;
        if (luid is { } requested && Luid != requested) throw new InvalidOperationException("D3D11 adapter LUID mismatch.");
        Adapter = selected ?? build.Add(actual.QueryInterface<IDXGIAdapter1>());
        Fence = build.Add(new GpuFence(Device, Context, fault));
        if (fenceSync)
        {
            // keyed 経路はこれらを参照しないため、keyed の挙動は変わらない。
            device1 = Device.QueryInterfaceOrNull<ID3D11Device1>(); if (device1 != null) build.Add(device1);
            device5 = Device.QueryInterfaceOrNull<ID3D11Device5>(); if (device5 != null) build.Add(device5);
            context4 = Context.QueryInterfaceOrNull<ID3D11DeviceContext4>(); if (context4 != null) build.Add(context4);
            if (device1 == null || device5 == null || context4 == null)
                throw new InvalidOperationException("fence source sync unsupported: device lacks ID3D11Device1/ID3D11Device5/ID3D11DeviceContext4 (D3D11.4).");
        }
        build.Commit();
    }

    public ID3D11Device1 Device1 => device1 ?? throw new InvalidOperationException("Device was not created for fence source sync.");
    public ID3D11Device5 Device5 => device5 ?? throw new InvalidOperationException("Device was not created for fence source sync.");
    public ID3D11DeviceContext4 Context4 => context4 ?? throw new InvalidOperationException("Device was not created for fence source sync.");

    public ID3D11Texture2D Texture(int width, int height, SourceSharing sharing)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = sharing switch
            {
                SourceSharing.KeyedMutex => ResourceOptionFlags.SharedKeyedMutex,
                SourceSharing.FenceNt => ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
                _ => ResourceOptionFlags.None
            }
        };
        return Device.CreateTexture2D(description);
    }

    public void Dispose()
    {
        Fence.Dispose(); context4?.Dispose(); device5?.Dispose(); device1?.Dispose();
        Context.ClearState(); Context.Flush(); Context.Dispose(); Device.Dispose(); Adapter.Dispose(); Factory.Dispose();
    }
}

internal static class Displays
{
    public static IDXGIAdapter1 FindAdapter(IDXGIFactory1 factory, long luid)
    {
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (adapter.Description1.Luid == luid) return adapter;
            adapter.Dispose();
        }
        throw new InvalidOperationException("Selected GPU adapter disappeared.");
    }
}

/// <summary>合成デバイスの共有フェンス。値は画像 ID（単調増加）。NT ハンドルは送信側が開いた後に作成側が閉じる。</summary>
internal sealed class SharedFence : IDisposable
{
    public ID3D11Fence Fence { get; }
    private IntPtr handle;
    private readonly FenceSequence sequence = new();

    public SharedFence(GpuDevice gpu)
    {
        using var build = new ConstructionScope();
        Fence = build.Add(gpu.Device5.CreateFence(0, FenceFlags.Shared));
        handle = Fence.CreateSharedHandle(null, null!); // Vortice ラッパーが GENERIC_ALL を付ける。
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Fence shared handle was not created.");
        build.Commit();
    }

    public ID3D11Fence Open(GpuDevice other)
    {
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Fence handle already closed.");
        return other.Device5.OpenSharedFence<ID3D11Fence>(handle);
    }

    public void Signal(GpuDevice gpu, long value) { sequence.Next(value); gpu.Context4.Signal(Fence, (ulong)value); }
    public void CloseHandle() { if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; } }
    public void Dispose() { CloseHandle(); Fence.Dispose(); }
}

/// <summary>送信側の共有フェンス読み取り。GPU キューの待ちで、CPU はブロックしない。</summary>
internal sealed class SharedFenceReader(GpuDevice gpu, ID3D11Fence opened) : IDisposable
{
    public void Wait(long value) => gpu.Context4.Wait(opened, (ulong)value);
    public void Dispose() => opened.Dispose();
}

/// <summary>フェンス値を GPU に出す前に厳密増加を検査する。</summary>
internal sealed class FenceSequence
{
    public long Last { get; private set; }
    public long Next(long value)
    {
        if (value <= Last) throw new InvalidOperationException($"Fence value {value} does not exceed the last signalled value {Last}.");
        Last = value; return value;
    }
}

/// <summary>
/// GPU 完了確認。期限超過を資源解放の理由にしない。S_OK+BOOL かデバイス損失だけが待ちを終える。
/// D28: 100ms スライスを超えたら fault にせず false を返し、呼び出し側が tick を skip して
/// 次 tick で再試行できるようにする。fault（Playback unavailable）はデバイス消失か、
/// 未完了が GpuCompletionPolicy.FaultAfterSeconds 秒連続したときだけ。
/// 試作 GpuFence を移植。
/// </summary>
internal sealed class GpuFence : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11Query query;
    private readonly Action<string> fault;
    private readonly GpuCompletionPolicy policy;
    private long lastRemovedCheck;
    private bool stuckFaulted;

    public GpuFence(ID3D11Device device, ID3D11DeviceContext context, Action<string> fault)
    {
        this.device = device; this.context = context; this.fault = fault;
        policy = new GpuCompletionPolicy(System.Diagnostics.Stopwatch.Frequency);
        query = device.CreateQuery(new QueryDescription(QueryType.Event));
    }

    /// <summary>
    /// 完了まで待つ。deferOnSliceExpiry=false は完了（または fault 後の完了・デバイス消失）までブロックする。
    /// true のときは 1 スライスで打ち切り、未完了なら false（呼び出し側がこの tick を skip して再試行）。
    /// 例外はデバイス消失のみ（GpuDeviceLostException）。
    /// </summary>
    public unsafe bool Wait(string stage, bool deferOnSliceExpiry = false)
    {
        context.End(query); context.Flush();
        long sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            int done = 0;
            int hr = context.GetData(query, (IntPtr)(&done), 4, AsyncGetDataFlags.DoNotFlush).Code;
            bool completed = hr == 0 && done != 0;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool removed = false;
            if (!completed && System.Diagnostics.Stopwatch.GetElapsedTime(lastRemovedCheck).TotalMilliseconds >= GpuCompletionPolicy.SliceMilliseconds)
            {
                lastRemovedCheck = now;
                removed = device.DeviceRemovedReason.Code < 0;
            }
            switch (policy.Decide(completed, removed, now))
            {
                case GpuWaitDecision.Completed:
                    return true;
                case GpuWaitDecision.DeviceLost:
                    throw new GpuDeviceLostException($"{stage}: device removed 0x{device.DeviceRemovedReason.Code:X8}");
                case GpuWaitDecision.Stuck:
                    if (!stuckFaulted)
                    {
                        stuckFaulted = true;
                        fault($"{stage}: GPU completion pending >{GpuCompletionPolicy.FaultAfterSeconds}s; new work stopped, retaining resources until completion/device loss.");
                    }
                    Thread.Sleep(1);
                    break;
                default:
                    if (deferOnSliceExpiry && policy.SliceExpired(sliceStart, now)) return false;
                    Thread.Yield();
                    break;
            }
        }
    }

    /// <summary>非ブロッキングの完了確認（保留した tick の回収用）。直近の Wait が仕掛けた時点まで完了したか。</summary>
    public unsafe bool IsComplete()
    {
        int done = 0;
        int hr = context.GetData(query, (IntPtr)(&done), 4, AsyncGetDataFlags.DoNotFlush).Code;
        return hr == 0 && done != 0;
    }

    public void Dispose() => query.Dispose();
}

internal sealed class GpuDeviceLostException(string message) : Exception(message);

/// <summary>合成 pool の1面。テクスチャ＋SRV（＋必要なら RTV / keyed mutex / NT ハンドル）を持つ。</summary>
internal sealed class Surface : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11RenderTargetView? Target { get; }
    public ID3D11ShaderResourceView View { get; }
    public IDXGIKeyedMutex? Mutex { get; }
    public IntPtr Handle { get; private set; }
    private readonly bool ownsHandle;

    public Surface(GpuDevice gpu, ID3D11Texture2D texture, bool createTarget, SourceSharing sharing)
    {
        using var build = new ConstructionScope();
        Texture = build.Add(texture);
        if (createTarget) Target = build.Add(gpu.Device.CreateRenderTargetView(texture));
        View = build.Add(gpu.Device.CreateShaderResourceView(texture));
        if (sharing == SourceSharing.KeyedMutex)
        {
            Mutex = build.Add(texture.QueryInterface<IDXGIKeyedMutex>());
            using var resource = texture.QueryInterface<IDXGIResource>();
            Handle = resource.SharedHandle; // legacy ハンドルは DXGI 所有。CloseHandle しない。
        }
        else if (sharing == SourceSharing.FenceNt)
        {
            using var resource = texture.QueryInterface<IDXGIResource1>();
            Handle = resource.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read, null!);
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("Shared NT handle was not created.");
            ownsHandle = true;
        }
        build.Commit();
    }

    // fence モードのみ: 送信側が開いた後に作成側が NT ハンドルを閉じる。
    public void CloseSharedHandle()
    {
        if (ownsHandle && Handle != IntPtr.Zero) { Native.CloseHandle(Handle); Handle = IntPtr.Zero; }
    }

    public bool Acquire()
    {
        if (Mutex == null) return true;
        int hr = Native.AcquireKeyedMutex(Mutex.NativePointer);
        if (hr == 0) return true;
        if (hr == 258) return false;
        if (hr == 128) throw new GpuDeviceLostException("Keyed mutex abandoned: shared resource no longer usable.");
        throw new COMException($"AcquireSync failed 0x{hr:X8}", hr);
    }

    public void Release()
    {
        if (Mutex == null) return;
        int hr = Native.ReleaseKeyedMutex(Mutex.NativePointer);
        if (hr != 0) throw new COMException($"ReleaseSync failed 0x{hr:X8}", hr);
    }

    public void Dispose() { CloseSharedHandle(); Mutex?.Dispose(); View.Dispose(); Target?.Dispose(); Texture.Dispose(); }
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    // IDXGIKeyedMutex::AcquireSync は IUnknown/IDXGIObject/IDXGIDeviceSubObject の後ろの slot8。
    // Vortice の void ラッパーは WAIT_TIMEOUT/WAIT_ABANDONED の正の HRESULT を落とすため直接呼ぶ。
    internal static unsafe int ReleaseKeyedMutex(IntPtr self)
    {
        var method = (delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)(*(IntPtr**)self)[9];
        return method(self, 0);
    }

    internal static unsafe int AcquireKeyedMutex(IntPtr self)
    {
        var method = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)(*(IntPtr**)self)[8];
        return method(self, 0, 0);
    }
}

internal sealed class ConstructionScope : IDisposable
{
    private readonly List<IDisposable> items = new();
    public T Add<T>(T item) where T : IDisposable { items.Add(item); return item; }
    public void Commit() => items.Clear();
    public void Dispose() { for (int i = items.Count - 1; i >= 0; i--) items[i].Dispose(); items.Clear(); }
}
