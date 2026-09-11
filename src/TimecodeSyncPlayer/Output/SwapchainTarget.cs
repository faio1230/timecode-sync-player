using System.ComponentModel;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TimecodeSyncPlayer.Output;

internal enum DisplayWaitResult { Ready, Timeout, Cancelled }

/// <summary>
/// 全画面の flip-discard swapchain。latency waitable と GetFrameStatistics を持つ。
/// 生成・Present・統計取得はすべて GPU worker だけが行う。試作 DisplayTarget を移植。
/// </summary>
internal sealed class SwapchainTarget : IDisposable
{
    private readonly IDXGISwapChain2 swap;
    private readonly IntPtr ready;
    private readonly ID3D11Device device;
    public ID3D11RenderTargetView Target { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public PresentReadyGate Readiness { get; } = new();

    public SwapchainTarget(GpuDevice gpu, IntPtr hwnd)
    {
        using var build = new ConstructionScope();
        device = gpu.Device;
        if (!Native.GetClientRect(hwnd, out var r)) throw new Win32Exception();
        Width = r.Right; Height = r.Bottom;
        var description = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject
        };
        using var first = gpu.Factory.CreateSwapChainForHwnd(gpu.Device, hwnd, description);
        swap = build.Add(first.QueryInterface<IDXGISwapChain2>());
        swap.MaximumFrameLatency = 1;
        ready = swap.FrameLatencyWaitableObject;
        if (ready == IntPtr.Zero) throw new InvalidOperationException("No frame latency waitable object.");
        using var buffer = swap.GetBuffer<ID3D11Texture2D>(0);
        Target = build.Add(gpu.Device.CreateRenderTargetView(buffer));
        gpu.Factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter).CheckError();
        build.Commit();
    }

    /// <summary>
    /// 子 HWND の最終寸法へ swapchain を追従させる。GPU worker が lease を持たないタイミングで呼ぶ。
    /// flip-discard のフラグは作成時と同じものを渡す。
    /// </summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == Width && height == Height)) return;
        Target.Dispose();
        swap.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
        Width = width;
        Height = height;
        using var buffer = swap.GetBuffer<ID3D11Texture2D>(0);
        Target = device.CreateRenderTargetView(buffer);
    }

    public bool WaitReady(int timeoutMs)
    {
        uint result = Native.WaitForSingleObject(ready, (uint)timeoutMs);
        if (result == 0) return true;
        if (result == 258) return false;
        throw new Win32Exception();
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
                0 => DisplayWaitResult.Cancelled, // 同時シグナルなら index0 が優先。
                1 => DisplayWaitResult.Ready,
                258 => DisplayWaitResult.Timeout,
                _ => throw new Win32Exception(Marshal.GetLastWin32Error(), $"WaitForMultipleObjects returned 0x{result:X8}.")
            };
        }
        finally { if (referenced) stopHandle.DangerousRelease(); }
    }

    public int Present()
    {
        Readiness.ConsumeForPresent();
        var result = swap.Present(1, PresentFlags.None);
        result.CheckError();
        return result.Code;
    }

    // GPU worker 専用。この swapchain の Present 呼び出し回数。統計の PresentCount と同じ採番。
    public uint GetLastPresentCount() => swap.LastPresentCount;

    private const int FrameStatisticsDisjoint = unchecked((int)0x887A000B);
    public bool StatisticsDisjoint { get; private set; }

    // GPU worker 専用。lease・mutex・フェンス待ちを持たずに呼ぶ。disjoint は false（1回だけフラグ）、それ以外の失敗は例外。
    public bool TryGetFrameStatistics(out FrameStatistics stats)
    {
        var result = swap.GetFrameStatistics(out stats);
        if (result.Success) return true;
        if (result.Code == FrameStatisticsDisjoint) { StatisticsDisjoint = true; return false; }
        result.CheckError(); return false;
    }

    public void Dispose()
    {
        Readiness.Discard();
        Target.Dispose();
        swap.Dispose();
        Native.CloseHandle(ready);
    }
}
