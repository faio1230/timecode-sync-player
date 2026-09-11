using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// 合成・表示の HLSL パイプライン。テストカード（外周枠・十字・動くマーカー・24bit 更新番号）を描く Pattern、
/// 画像をキャンバスへ配置して黒で埋める Display（UV 矩形で SourceCrop をサンプル）を持つ。
/// 試作 scripts/GpuOutputProbe の ShaderPipeline を移植。
/// </summary>
internal sealed class ShaderPipeline : IDisposable
{
    private readonly GpuDevice gpu;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pattern, display;
    private readonly ID3D11Buffer constants, uvConstants;
    private readonly ID3D11SamplerState sampler;

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters { public uint Id; public float Seconds, Width, Height; }

    // Display は uv' = uvOffset + uv * uvScale をサンプルする。同一値なら画像全体。
    [StructLayout(LayoutKind.Sequential)]
    private struct UvRect { public float OffsetX, OffsetY, ScaleX, ScaleY; }

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
        vertex = build.Add(gpu.Device.CreateVertexShader(Compiler.Compile(Source, "VS", "OutputEngine.hlsl", "vs_5_0").Span));
        pattern = build.Add(gpu.Device.CreatePixelShader(Compiler.Compile(Source, "Pattern", "OutputEngine.hlsl", "ps_5_0").Span));
        display = build.Add(gpu.Device.CreatePixelShader(Compiler.Compile(Source, "Display", "OutputEngine.hlsl", "ps_5_0").Span));
        constants = build.Add(gpu.Device.CreateBuffer(16, BindFlags.ConstantBuffer));
        uvConstants = build.Add(gpu.Device.CreateBuffer(16, BindFlags.ConstantBuffer));
        sampler = build.Add(gpu.Device.CreateSamplerState(SamplerDescription.LinearClamp));
        build.Commit();
    }

    public static void ValidateShaders()
    {
        _ = Compiler.Compile(Source, "VS", "OutputEngine.hlsl", "vs_5_0");
        _ = Compiler.Compile(Source, "Pattern", "OutputEngine.hlsl", "ps_5_0");
        _ = Compiler.Compile(Source, "Display", "OutputEngine.hlsl", "ps_5_0");
    }

    private void Begin(ID3D11RenderTargetView target, int width, int height, ID3D11PixelShader pixel)
    {
        var c = gpu.Context;
        c.OMSetRenderTargets(target); c.RSSetViewport(0, 0, width, height);
        c.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); c.VSSetShader(vertex); c.PSSetShader(pixel);
    }

    /// <summary>不透明な黒でキャンバスをクリアする。</summary>
    public void Clear(ID3D11RenderTargetView target)
    {
        gpu.Context.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
    }

    /// <summary>テストカード（Pattern）をキャンバス全面へ描く。</summary>
    public void Compose(Surface target, int width, int height, ImageStamp image, long origin)
    {
        Begin(target.Target!, width, height, pattern);
        var p = new Parameters { Id = (uint)image.Id, Seconds = (float)((image.GeneratedQpc - origin) / (double)Stopwatch.Frequency), Width = width, Height = height };
        gpu.Context.UpdateSubresource(in p, constants); gpu.Context.PSSetConstantBuffer(0, constants);
        gpu.Context.Draw(3, 0); gpu.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    /// <summary>キャンバス全体を表示先へレターボックスで描く（全画面用）。</summary>
    public void Display(Surface source, ID3D11RenderTargetView target, int width, int height, int canvasWidth, int canvasHeight)
    {
        float scale = Math.Min(width / (float)canvasWidth, height / (float)canvasHeight);
        float w = canvasWidth * scale, h = canvasHeight * scale;
        Blit(source, target, width, height, (width - w) / 2, (height - h) / 2, w, h, WholeImage);
    }

    /// <summary>ソース画像をキャンバスへ配置する（docs/CANVAS-PLACEMENT-SPEC.md）。段階 2 以降で使う。</summary>
    public void Place(Surface source, int sourceWidth, int sourceHeight, ID3D11RenderTargetView canvas, int canvasWidth, int canvasHeight, Placement placement)
    {
        var d = placement.Destination; var c = placement.SourceCrop;
        var uv = new UvRect { OffsetX = (float)(c.X / sourceWidth), OffsetY = (float)(c.Y / sourceHeight), ScaleX = (float)(c.Width / sourceWidth), ScaleY = (float)(c.Height / sourceHeight) };
        Blit(source, canvas, canvasWidth, canvasHeight, (float)d.X, (float)d.Y, (float)d.Width, (float)d.Height, uv);
    }

    /// <summary>借用テクスチャから作った SRV をキャンバスへ配置する（GStreamer リースの直接描画用）。</summary>
    public void PlaceView(ID3D11ShaderResourceView source, int sourceWidth, int sourceHeight, ID3D11RenderTargetView canvas,
        int canvasWidth, int canvasHeight, Placement placement)
    {
        var d = placement.Destination; var c = placement.SourceCrop;
        var uv = new UvRect { OffsetX = (float)(c.X / sourceWidth), OffsetY = (float)(c.Y / sourceHeight), ScaleX = (float)(c.Width / sourceWidth), ScaleY = (float)(c.Height / sourceHeight) };
        Blit(source, canvas, canvasWidth, canvasHeight, (float)d.X, (float)d.Y, (float)d.Width, (float)d.Height, uv);
    }

    private void Blit(Surface source, ID3D11RenderTargetView target, int width, int height, float x, float y, float w, float h, UvRect uv)
        => Blit(source.View, target, width, height, x, y, w, h, uv);

    private void Blit(ID3D11ShaderResourceView source, ID3D11RenderTargetView target, int width, int height, float x, float y, float w, float h, UvRect uv)
    {
        Begin(target, width, height, display);
        gpu.Context.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
        gpu.Context.RSSetViewport(x, y, w, h);
        gpu.Context.UpdateSubresource(in uv, uvConstants); gpu.Context.PSSetConstantBuffer(1, uvConstants);
        gpu.Context.PSSetShaderResource(0, source); gpu.Context.PSSetSampler(0, sampler); gpu.Context.Draw(3, 0);
        gpu.Context.PSSetShaderResource(0, null!); gpu.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    public void Dispose() { sampler.Dispose(); uvConstants.Dispose(); constants.Dispose(); display.Dispose(); pattern.Dispose(); vertex.Dispose(); }
}
