using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace GpuOutputProbe;

internal sealed record DisplayInfo(int Index, string DeviceName, int X, int Y, int Width, int Height, uint RefreshHz, long AdapterLuid, string AdapterDescription);
internal static class Displays
{
    public static List<DisplayInfo> Enumerate()
    {
        var result = new List<DisplayInfo>();
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
        {
            using (adapter)
            for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
            {
                using (output)
                {
                    var d = output.Description;
                    if (!d.AttachedToDesktop) continue;
                    var r = d.DesktopCoordinates;
                    var mode = new Native.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>() };
                    uint hz = Native.EnumDisplaySettings(d.DeviceName, -1, ref mode) ? mode.dmDisplayFrequency : 0;
                    result.Add(new(result.Count, d.DeviceName, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, hz, adapter.Description1.Luid, adapter.Description1.Description));
                }
            }
        }
        return result;
    }
    public static IDXGIAdapter1 FindAdapter(IDXGIFactory1 factory, long luid)
    {
        for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
        { if (a.Description1.Luid == luid) return a; a.Dispose(); }
        throw new InvalidOperationException("Selected GPU adapter disappeared.");
    }
}

// None: private texture. KeyedMutex: legacy DXGI handle + keyed mutex. FenceNt: NT handle without keyed mutex; GPU order comes from a shared fence.
internal enum SourceSharing { None, KeyedMutex, FenceNt }

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
    public GpuDevice(long luid, Action<string> fault, bool fenceSync = false)
    {
        using var build = new ConstructionScope();
        Factory = build.Add(CreateDXGIFactory1<IDXGIFactory2>());
        Adapter = build.Add(Displays.FindAdapter(Factory, luid));
        var created = D3D11CreateDevice(Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out Device, out Context);
        if (Device != null) build.Add(Device); if (Context != null) build.Add(Context); created.CheckError();
        if (Device == null || Context == null) throw new InvalidOperationException("D3D11 creation returned no device/context.");
        using var dxgi = Device!.QueryInterface<IDXGIDevice>();
        using var actual = dxgi.GetAdapter();
        Luid = actual.Description.Luid;
        if (Luid != luid) throw new InvalidOperationException("D3D11 adapter LUID mismatch.");
        Fence = build.Add(new GpuFence(Device!, Context!, fault));
        if (fenceSync)
        {
            // Keyed mode never queries these, so its device/context behavior is unchanged.
            device1 = Device!.QueryInterfaceOrNull<ID3D11Device1>(); if (device1 != null) build.Add(device1);
            device5 = Device!.QueryInterfaceOrNull<ID3D11Device5>(); if (device5 != null) build.Add(device5);
            context4 = Context!.QueryInterfaceOrNull<ID3D11DeviceContext4>(); if (context4 != null) build.Add(context4);
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
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
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

// Compose-device fence shared to the sender device. Fence value == image id (monotonic); the creator owns the NT handle
// only until the sender has opened the fence, then closes it.
internal sealed class SharedFence : IDisposable
{
    public ID3D11Fence Fence { get; }
    private IntPtr handle;
    private readonly FenceSequence sequence = new();
    public SharedFence(GpuDevice gpu)
    {
        using var build = new ConstructionScope();
        Fence = build.Add(gpu.Device5.CreateFence(0, FenceFlags.Shared));
        handle = Fence.CreateSharedHandle(null, null!); // GENERIC_ALL access inside the Vortice wrapper.
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

// Sender-side view of the shared fence: a GPU-queue wait on the sender context, never a CPU block.
internal sealed class SharedFenceReader(GpuDevice gpu, ID3D11Fence opened) : IDisposable
{
    public void Wait(long value) => gpu.Context4.Wait(opened, (ulong)value);
    public void Dispose() => opened.Dispose();
}

// Pure monotonic check for fence values so a regression is rejected before reaching the GPU.
internal sealed class FenceSequence
{
    public long Last { get; private set; }
    public long Next(long value)
    {
        if (value <= Last) throw new InvalidOperationException($"Fence value {value} does not exceed the last signalled value {Last}.");
        Last = value; return value;
    }
}

// Completion never treats a timeout as permission to recycle resources. Only S_OK+BOOL or device loss ends the wait.
internal sealed class GpuFence : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11Query query;
    private readonly Action<string> fault;
    public GpuFence(ID3D11Device device, ID3D11DeviceContext context, Action<string> fault)
    { this.device = device; this.context = context; this.fault = fault; query = device.CreateQuery(new QueryDescription(QueryType.Event)); }
    public unsafe void Wait(string stage)
    {
        context.End(query); context.Flush();
        long start = Stopwatch.GetTimestamp(); long lastRemovedCheck = start; bool warned = false;
        while (true)
        {
            int done = 0;
            int hr = context.GetData(query, (IntPtr)(&done), 4, AsyncGetDataFlags.DoNotFlush).Code;
            if (hr == 0 && done != 0) return;
            if ((!warned && hr < 0) || Stopwatch.GetElapsedTime(lastRemovedCheck).TotalMilliseconds >= 100)
            {
                lastRemovedCheck = Stopwatch.GetTimestamp();
                int removed = device.DeviceRemovedReason.Code;
                if (removed < 0) throw new GpuDeviceLostException($"{stage}: device removed 0x{removed:X8}");
            }
            if (!warned && (hr < 0 || Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 100))
            {
                warned = true;
                fault($"{stage}: GPU completion pending >100ms or GetData failed (0x{hr:X8}); new work stopped, retaining resources until completion/device loss.");
            }
            if (warned) Thread.Sleep(1); else Thread.Yield();
        }
    }
    public void Dispose() => query.Dispose();
}
internal sealed class GpuDeviceLostException(string message) : Exception(message);

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
            Handle = resource.SharedHandle; // Legacy handle belongs to DXGI. Never CloseHandle.
        }
        else if (sharing == SourceSharing.FenceNt)
        {
            using var resource = texture.QueryInterface<IDXGIResource1>();
            Handle = resource.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read, null!); // NT handle: owned here until closed.
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("Shared NT handle was not created.");
            ownsHandle = true;
        }
        build.Commit();
    }
    // Fence mode only: the creator closes its NT handle once the sender has opened the texture.
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

internal sealed class ShaderPipeline : IDisposable
{
    private readonly GpuDevice gpu;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pattern, display;
    private readonly ID3D11Buffer constants, uvConstants;
    private readonly ID3D11SamplerState sampler;
    [StructLayout(LayoutKind.Sequential)] private struct Parameters { public uint Id; public float Seconds, Width, Height; }
    // Display samples uv' = uvOffset + uv * uvScale: the sampled source sub-rectangle in normalized coordinates (identity = whole image).
    [StructLayout(LayoutKind.Sequential)] private struct UvRect { public float OffsetX, OffsetY, ScaleX, ScaleY; }
    private static readonly UvRect WholeImage = new() { ScaleX = 1, ScaleY = 1 };
    private const string Source = """
        cbuffer Parameters : register(b0) { uint frameId; float seconds; float width; float height; };
        cbuffer Uv : register(b1) { float2 uvOffset; float2 uvScale; };
        struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
        Vertex VS(uint id : SV_VertexID) {
            Vertex o; o.uv = float2((id << 1) & 2, id & 2);
            o.position = float4(o.uv * float2(2,-2) + float2(-1,1),0,1); return o;
        }
        float4 Pattern(Vertex v) : SV_Target {
            float2 uv = v.uv;
            uint bar = min(7u,(uint)(uv.x * 8));
            float3 c = float3((bar & 4) ? 1 : 0, (bar & 2) ? 1 : 0, (bar & 1) ? 1 : 0);
            if (uv.y > .50) c *= .35;
            if (min(uv.x,1-uv.x) < 3/width || min(uv.y,1-uv.y) < 3/height) c=1;
            if (abs(uv.x-.5) < 1/width || abs(uv.y-.5) < 1/height) c=1;
            if (abs(uv.x-frac(seconds*.2)) < .007 && uv.y > .52 && uv.y < .74) c=float3(1,1,1);
            // 24-bit binary image ID: least significant bit on the left. Black gutters make mixed frames visible.
            if (uv.y > .80 && uv.y < .94 && uv.x > .02 && uv.x < .98) {
                float cell = (uv.x-.02)/.96*24;
                uint bit = (uint)cell;
                c = frac(cell)<.08 ? float3(.2,.2,.2) : (((frameId>>bit)&1) ? float3(1,1,1) : float3(0,0,0));
            }
            return float4(c,1);
        }
        Texture2D image : register(t0); SamplerState linearClamp : register(s0);
        float4 Display(Vertex v) : SV_Target { return float4(image.Sample(linearClamp, uvOffset + v.uv * uvScale).rgb,1); }
        """;
    public ShaderPipeline(GpuDevice gpu)
    {
        this.gpu = gpu;
        using var build = new ConstructionScope();
        vertex = build.Add(gpu.Device.CreateVertexShader(Compiler.Compile(Source, "VS", "Probe.hlsl", "vs_5_0").Span));
        pattern = build.Add(gpu.Device.CreatePixelShader(Compiler.Compile(Source, "Pattern", "Probe.hlsl", "ps_5_0").Span));
        display = build.Add(gpu.Device.CreatePixelShader(Compiler.Compile(Source, "Display", "Probe.hlsl", "ps_5_0").Span));
        constants = build.Add(gpu.Device.CreateBuffer(16, BindFlags.ConstantBuffer));
        uvConstants = build.Add(gpu.Device.CreateBuffer(16, BindFlags.ConstantBuffer));
        sampler = build.Add(gpu.Device.CreateSamplerState(SamplerDescription.LinearClamp));
        build.Commit();
    }
    public static void ValidateShaders()
    {
        _ = Compiler.Compile(Source, "VS", "Probe.hlsl", "vs_5_0");
        _ = Compiler.Compile(Source, "Pattern", "Probe.hlsl", "ps_5_0");
        _ = Compiler.Compile(Source, "Display", "Probe.hlsl", "ps_5_0");
    }
    private void Begin(ID3D11RenderTargetView target, int width, int height, ID3D11PixelShader pixel)
    {
        var c = gpu.Context;
        c.OMSetRenderTargets(target); c.RSSetViewport(0, 0, width, height);
        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); c.VSSetShader(vertex); c.PSSetShader(pixel);
    }
    public void Compose(Surface target, int width, int height, ImageStamp image, long origin)
    {
        Begin(target.Target!, width, height, pattern);
        var p = new Parameters { Id = (uint)image.Id, Seconds = (float)((image.GeneratedQpc - origin) / (double)Stopwatch.Frequency), Width = width, Height = height };
        gpu.Context.UpdateSubresource(in p, constants); gpu.Context.PSSetConstantBuffer(0, constants);
        gpu.Context.Draw(3, 0); gpu.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }
    // Canvas to display: the whole canvas letterboxed into the target (unchanged behavior; the whole image is sampled).
    public void Display(Surface source, ID3D11RenderTargetView target, int width, int height, int canvasWidth, int canvasHeight)
    {
        float scale = Math.Min(width / (float)canvasWidth, height / (float)canvasHeight);
        float w = canvasWidth * scale, h = canvasHeight * scale;
        Blit(source, target, width, height, (width-w)/2, (height-h)/2, w, h, WholeImage);
    }
    // Source to canvas (docs/CANVAS-PLACEMENT-SPEC.md): black outside placement.Destination (the viewport, real-valued; the rasterizer
    // rounds), and only placement.SourceCrop is sampled, mapped to normalized coordinates of the source image.
    public void Place(Surface source, int sourceWidth, int sourceHeight, ID3D11RenderTargetView canvas, int canvasWidth, int canvasHeight, Placement placement)
    {
        var d = placement.Destination; var c = placement.SourceCrop;
        var uv = new UvRect { OffsetX = (float)(c.X / sourceWidth), OffsetY = (float)(c.Y / sourceHeight), ScaleX = (float)(c.Width / sourceWidth), ScaleY = (float)(c.Height / sourceHeight) };
        Blit(source, canvas, canvasWidth, canvasHeight, (float)d.X, (float)d.Y, (float)d.Width, (float)d.Height, uv);
    }
    private void Blit(Surface source, ID3D11RenderTargetView target, int width, int height, float x, float y, float w, float h, UvRect uv)
    {
        Begin(target, width, height, display);
        gpu.Context.ClearRenderTargetView(target, new Color4(0,0,0,1));
        gpu.Context.RSSetViewport(x, y, w, h);
        gpu.Context.UpdateSubresource(in uv, uvConstants); gpu.Context.PSSetConstantBuffer(1, uvConstants);
        gpu.Context.PSSetShaderResource(0, source.View); gpu.Context.PSSetSampler(0, sampler); gpu.Context.Draw(3,0);
        gpu.Context.PSSetShaderResource(0, null!); gpu.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }
    public void Dispose() { sampler.Dispose(); uvConstants.Dispose(); constants.Dispose(); display.Dispose(); pattern.Dispose(); vertex.Dispose(); }
}

internal sealed class DisplayTarget : IDisposable
{
    private readonly IDXGISwapChain2 swap;
    private readonly IntPtr ready;
    public ID3D11RenderTargetView Target { get; }
    public int Width { get; }
    public int Height { get; }
    public PresentReadyGate Readiness { get; } = new();
    public DisplayTarget(GpuDevice gpu, IntPtr hwnd)
    {
        using var build = new ConstructionScope();
        if (!Native.GetClientRect(hwnd, out var r)) throw new System.ComponentModel.Win32Exception();
        Width = r.Right; Height = r.Bottom;
        var description = new SwapChainDescription1 { Width = (uint)Width, Height = (uint)Height, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore, Flags = SwapChainFlags.FrameLatencyWaitableObject };
        using var first = gpu.Factory.CreateSwapChainForHwnd(gpu.Device, hwnd, description);
        swap = build.Add(first.QueryInterface<IDXGISwapChain2>());
        swap.MaximumFrameLatency = 1;
        ready = swap.FrameLatencyWaitableObject;
        if (ready == IntPtr.Zero) throw new InvalidOperationException("No frame latency waitable object.");
        build.Add(new NativeHandle(ready));
        using var buffer = swap.GetBuffer<ID3D11Texture2D>(0);
        Target = build.Add(gpu.Device.CreateRenderTargetView(buffer));
        gpu.Factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter).CheckError();
        build.Commit();
    }
    public bool WaitReady(int timeoutMs)
    {
        uint result = Native.WaitForSingleObject(ready, (uint)timeoutMs);
        if (result == 0) return true;
        if (result == 258) return false;
        throw new System.ComponentModel.Win32Exception();
    }
    public unsafe DisplayWaitResult WaitReadyOrStop(WaitHandle stop, int timeoutMs)
    {
        var stopHandle = stop.SafeWaitHandle;
        bool referenced = false;
        try
        {
            stopHandle.DangerousAddRef(ref referenced);
            IntPtr* handles = stackalloc IntPtr[2] { stopHandle.DangerousGetHandle(), ready };
            uint result = Native.WaitForMultipleObjects(2, handles, false, (uint)timeoutMs);
            return result switch
            {
                0 => DisplayWaitResult.Cancelled, // Index0 wins when stop and readiness are simultaneously signaled.
                1 => DisplayWaitResult.Ready,
                258 => DisplayWaitResult.Timeout,
                _ => throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"WaitForMultipleObjects returned 0x{result:X8}.")
            };
        }
        finally { if (referenced) stopHandle.DangerousRelease(); }
    }
    public int Present() { Readiness.ConsumeForPresent(); var result = swap.Present(1, PresentFlags.None); result.CheckError(); return result.Code; }
    // GPU worker only. Counts Present calls on this swapchain; the statistics' PresentCount refers to the same numbering.
    public uint GetLastPresentCount() => swap.LastPresentCount; // IDXGISwapChain::GetLastPresentCount; the wrapper throws on failure.
    private const int FrameStatisticsDisjoint = unchecked((int)0x887A000B);
    public bool StatisticsDisjoint { get; private set; }
    // GPU worker only; call without any lease, keyed mutex, or fence wait held. Disjoint returns false (flag set once); other failures throw.
    public bool TryGetFrameStatistics(out FrameStatistics stats)
    {
        var result = swap.GetFrameStatistics(out stats);
        if (result.Success) return true;
        if (result.Code == FrameStatisticsDisjoint) { StatisticsDisjoint = true; return false; }
        result.CheckError(); return false;
    }
    public void Dispose() { Readiness.Discard(); Target.Dispose(); swap.Dispose(); Native.CloseHandle(ready); }
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE info);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern unsafe uint WaitForMultipleObjects(uint count, IntPtr* handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
}




internal sealed class ConstructionScope : IDisposable
{
    private readonly List<IDisposable> items = new();
    public T Add<T>(T item) where T : IDisposable { items.Add(item); return item; }
    public void Commit() => items.Clear();
    public void Dispose() { for (int i = items.Count - 1; i >= 0; i--) items[i].Dispose(); items.Clear(); }
}
internal sealed class NativeHandle(IntPtr handle) : IDisposable
{
    public void Dispose() => Native.CloseHandle(handle);
}
