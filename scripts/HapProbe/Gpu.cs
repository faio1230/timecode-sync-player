using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HapProbe;

/// <summary>圧縮テクスチャの GPU への上げ方。</summary>
internal enum UploadMethod
{
    /// <summary>DEFAULT テクスチャへ直接書く（UpdateSubresource）。</summary>
    Direct,
    /// <summary>STAGING テクスチャへ Map で書いてから DEFAULT へコピーする。</summary>
    Staging,
}

/// <summary>合成へ絵を渡す経路。</summary>
internal enum SourcePath
{
    /// <summary>GPU で BGRA に展開してから合成する（shim のリングに載せる形）。</summary>
    DecodeToBgra,
    /// <summary>圧縮テクスチャのまま合成でサンプリングする。</summary>
    CompressedPassthrough,
}

/// <summary>試作用の最小の D3D11。ウィンドウ 1 枚・スワップチェーン・全画面クアッドだけ。</summary>
internal sealed class Gpu : IDisposable
{
    private readonly int windowWidth, windowHeight;
    private readonly IntPtr hwnd;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11RenderTargetView backBufferView;
    private readonly ID3D11VertexShader vertexShader;
    private readonly Dictionary<string, ID3D11PixelShader> pixelShaders = new();
    private readonly ID3D11SamplerState sampler;
    // 全画面クアッドは頂点バッファ無しで作るため、面の向きが裏になりうる。既定の設定だと裏面が捨てられて
    // 何も描かれない（最初にこれで真っ黒になった）。向きを見ない設定にしておく。
    private readonly ID3D11RasterizerState rasterizer;

    private ID3D11Texture2D? source;          // BC（圧縮）テクスチャ
    private ID3D11ShaderResourceView? sourceView;
    private ID3D11Texture2D? staging;         // Map 用
    private ID3D11Texture2D? decoded;         // 経路 1 の BGRA 中間
    private ID3D11ShaderResourceView? decodedView;
    private ID3D11RenderTargetView? decodedTarget;
    private int sourceWidth, sourceHeight;

    public Gpu(int windowWidth = 1280, int windowHeight = 720)
    {
        this.windowWidth = windowWidth;
        this.windowHeight = windowHeight;
        hwnd = Win32.CreateProbeWindow(windowWidth, windowHeight);
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device? created, out ID3D11DeviceContext? createdContext).CheckError();
        device = created!;
        context = createdContext!;
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = (uint)windowWidth,
            Height = (uint)windowHeight,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
        };
        swapChain = factory.CreateSwapChainForHwnd(device, hwnd, description);
        using ID3D11Texture2D backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        backBufferView = device.CreateRenderTargetView(backBuffer);

        ReadOnlyMemory<byte> vsBlob = Compiler.Compile(Shaders.Source, "VsMain", "hap", "vs_5_0");
        vertexShader = device.CreateVertexShader(vsBlob.Span);
        foreach (string entry in new[] { Shaders.PsYCoCg, Shaders.PsPlain })
            pixelShaders[entry] = device.CreatePixelShader(Compiler.Compile(Shaders.Source, entry, "hap", "ps_5_0").Span);
        sampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue,
        });
        rasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            DepthClipEnable = true,
        });
    }

    public string AdapterName
    {
        get
        {
            using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            return adapter.Description.Description;
        }
    }

    /// <summary>素材に合わせてテクスチャを作り直す。</summary>
    public void PrepareFor(HapFrameSet set, SourcePath path)
    {
        ReleaseTextures();
        sourceWidth = set.Width; sourceHeight = set.Height;
        Format format = set.Format switch
        {
            HapTextureFormat.Dxt1 => Format.BC1_UNorm,
            HapTextureFormat.Dxt5 or HapTextureFormat.YCoCgDxt5 => Format.BC3_UNorm,
            HapTextureFormat.Bc7 => Format.BC7_UNorm,
            _ => throw new NotSupportedException($"未対応のテクスチャ形式: {set.Format}"),
        };
        var textureDescription = new Texture2DDescription
        {
            Width = (uint)set.Width,
            Height = (uint)set.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };
        source = device.CreateTexture2D(textureDescription);
        sourceView = device.CreateShaderResourceView(source);
        staging = device.CreateTexture2D(textureDescription with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
        if (path == SourcePath.DecodeToBgra)
        {
            decoded = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)set.Width,
                Height = (uint)set.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            decodedView = device.CreateShaderResourceView(decoded);
            decodedTarget = device.CreateRenderTargetView(decoded);
        }
    }

    /// <summary>展開済みの BC データを GPU へ上げる。</summary>
    public void Upload(byte[] data, int length, UploadMethod method, HapTextureFormat format)
    {
        int blockBytes = format == HapTextureFormat.Dxt1 ? 8 : 16;
        int rowPitch = (sourceWidth + 3) / 4 * blockBytes;
        int rows = (sourceHeight + 3) / 4;
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr pointer = handle.AddrOfPinnedObject();
            if (method == UploadMethod.Direct)
            {
                context.UpdateSubresource(source!, 0, null, pointer, (uint)rowPitch, 0);
                return;
            }
            MappedSubresource mapped = context.Map(staging!, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                byte* destination = (byte*)mapped.DataPointer;
                byte* origin = (byte*)pointer;
                if (mapped.RowPitch == (uint)rowPitch)
                {
                    Buffer.MemoryCopy(origin, destination, (long)mapped.RowPitch * rows, (long)rowPitch * rows);
                }
                else
                {
                    for (int y = 0; y < rows; y++)
                        Buffer.MemoryCopy(origin + (long)y * rowPitch, destination + (long)y * mapped.RowPitch, mapped.RowPitch, rowPitch);
                }
            }
            context.Unmap(staging!, 0);
            context.CopyResource(source!, staging!);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>経路 1: BC から BGRA へ展開する 1 パス。</summary>
    public void DecodePass(bool yCoCg)
    {
        context.OMSetRenderTargets(decodedTarget!);
        context.RSSetViewport(0, 0, sourceWidth, sourceHeight);
        Draw(sourceView!, yCoCg);
    }

    /// <summary>合成（全画面クアッドで表示面へ）。表示は <see cref="Present"/> で別に行う。</summary>
    public void Compose(SourcePath path, bool yCoCg)
    {
        context.OMSetRenderTargets(backBufferView);
        context.ClearRenderTargetView(backBufferView, new Vortice.Mathematics.Color4(0, 0, 0, 1));
        context.RSSetViewport(0, 0, windowWidth, windowHeight);
        bool passthrough = path == SourcePath.CompressedPassthrough;
        Draw(passthrough ? sourceView! : decodedView!, passthrough && yCoCg);
    }

    /// <summary>表示する。FlipDiscard なので、読み戻すならこの前に行う。</summary>
    public void Present() => swapChain.Present(0, PresentFlags.None);

    public void ComposeAndPresent(SourcePath path, bool yCoCg)
    {
        Compose(path, yCoCg);
        Present();
    }

    private void Draw(ID3D11ShaderResourceView view, bool yCoCg)
    {
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(null);
        context.RSSetState(rasterizer);
        context.VSSetShader(vertexShader);
        context.PSSetShader(pixelShaders[yCoCg ? Shaders.PsYCoCg : Shaders.PsPlain]);
        context.PSSetShaderResource(0, view);
        context.PSSetSampler(0, sampler);
        context.Draw(4, 0);
    }

    public void PumpMessages() => Win32.PumpMessages(hwnd);

    /// <summary>
    /// いま表示している絵を読み戻して PNG で保存する。**速度ではなく「絵が合っているか」を確かめるため**
    /// （YCoCg の戻し方を間違えていても速度は変わらないので、必ず目と数字の両方で見る）。
    /// </summary>
    public void SaveBackBuffer(string path)
    {
        using ID3D11Texture2D backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        Texture2DDescription description = backBuffer.Description;
        using ID3D11Texture2D readback = device.CreateTexture2D(description with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        context.CopyResource(readback, backBuffer);
        MappedSubresource mapped = context.Map(readback, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width = (int)description.Width, height = (int)description.Height;
            var pixels = new byte[width * height * 4];
            unsafe
            {
                byte* source = (byte*)mapped.DataPointer;
                for (int y = 0; y < height; y++)
                    Marshal.Copy((IntPtr)(source + (long)y * mapped.RowPitch), pixels, y * width * 4, width * 4);
            }
            Png.Write(path, width, height, pixels);
        }
        finally
        {
            context.Unmap(readback, 0);
        }
    }

    private void ReleaseTextures()
    {
        sourceView?.Dispose(); sourceView = null;
        source?.Dispose(); source = null;
        staging?.Dispose(); staging = null;
        decodedView?.Dispose(); decodedView = null;
        decodedTarget?.Dispose(); decodedTarget = null;
        decoded?.Dispose(); decoded = null;
    }

    public void Dispose()
    {
        ReleaseTextures();
        rasterizer.Dispose();
        sampler.Dispose();
        foreach (ID3D11PixelShader shader in pixelShaders.Values) shader.Dispose();
        vertexShader.Dispose();
        backBufferView.Dispose();
        swapChain.Dispose();
        context.Dispose();
        device.Dispose();
        Win32.DestroyProbeWindow(hwnd);
    }
}

internal static class Shaders
{
    public const string PsYCoCg = "PsYCoCg";
    public const string PsPlain = "PsPlain";

    /// <summary>
    /// 全画面クアッド（頂点バッファ無し）と 2 つの画素シェーダ。
    /// Hap Q は Scaled YCoCg を DXT5 に詰めた形式なので、サンプル後に RGB へ戻す
    /// （仕様: https://github.com/Vidvox/hap/blob/master/documentation/HapVideoDRAFT.md）。
    /// </summary>
    public const string Source = """
Texture2D Source : register(t0);
SamplerState Sampler : register(s0);

struct Vertex { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };

Vertex VsMain(uint id : SV_VertexID)
{
    float2 uv = float2((id << 1) & 2, id & 2);
    Vertex output;
    output.uv = float2(uv.x, 1.0 - uv.y);
    output.position = float4(uv * float2(2.0, 2.0) - float2(1.0, 1.0), 0.0, 1.0);
    return output;
}

float4 PsPlain(Vertex input) : SV_TARGET
{
    return Source.Sample(Sampler, input.uv);
}

float4 PsYCoCg(Vertex input) : SV_TARGET
{
    float4 texel = Source.Sample(Sampler, input.uv);
    float scale = (texel.b * (255.0 / 8.0)) + 1.0;
    float co = (texel.r - (0.5 * 256.0 / 255.0)) * scale;
    float cg = (texel.g - (0.5 * 256.0 / 255.0)) * scale;
    float y = texel.a;
    return float4(y + co - cg, y + cg, y - co - cg, 1.0);
}
""";
}

/// <summary>試作用の最小のウィンドウ。</summary>
internal static class Win32
{
    private const int WsOverlappedWindow = 0x00CF0000, WsVisible = 0x10000000;
    private static WndProcDelegate? keepAlive;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

    public static IntPtr CreateProbeWindow(int width, int height)
    {
        keepAlive = (h, m, w, l) => DefWindowProcW(h, m, w, l);
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(keepAlive),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "HapProbeWindow",
        };
        RegisterClassExW(ref wc);
        return CreateWindowExW(0, "HapProbeWindow", "HAP probe", WsOverlappedWindow | WsVisible,
            100, 100, width, height, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    public static void PumpMessages(IntPtr hwnd)
    {
        while (PeekMessageW(out Msg msg, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    public static void DestroyProbeWindow(IntPtr hwnd) => DestroyWindow(hwnd);
}
