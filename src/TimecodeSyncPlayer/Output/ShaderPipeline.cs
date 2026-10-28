using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// 合成・表示の HLSL パイプライン。テストカード（グリッド・正円・色／階調・流れるグラデーション）を描く Pattern、
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
        float3 Hue(float h) {
            return saturate(abs(frac(h + float3(0,2.0/3,1.0/3))*6-3)-1);
        }
        float Line(float distance, float thickness) {
            return 1-smoothstep(thickness*.5, thickness*.5+1, abs(distance));
        }
        bool Inside(float2 uv, float4 rect) {
            return all(uv >= rect.xy) && all(uv <= rect.zw);
        }
        // Compact 3x5 digits, drawn in the shader without font textures or UI-thread work.
        float Digit(float2 p, uint digit) {
            if (any(p < 0) || p.x >= 3 || p.y >= 5) return 0;
            static const uint glyphs[10] = {
                31599, 11415, 29671, 29647, 23497,
                31183, 31215, 29257, 31727, 31695
            };
            uint bit = 14-((uint)p.y*3+(uint)p.x);
            return (glyphs[min(digit,9u)] >> bit)&1;
        }
        float Dimension(float2 p, uint value) {
            if (any(p < 0) || p.x >= 19 || p.y >= 5) return 0;
            uint slot = (uint)(p.x/4);
            uint divisor = slot == 0 ? 10000 : slot == 1 ? 1000 : slot == 2 ? 100 : slot == 3 ? 10 : 1;
            if (value < divisor && slot < 4) return 0;
            return Digit(float2(p.x-slot*4,p.y), (value/divisor)%10);
        }
        float4 Pattern(Vertex v) : SV_Target {
            float2 uv = v.uv;
            float2 size = float2(width,height);
            float2 p = (uv-.5)*size;
            float shortSide = min(width,height);
            float stroke = max(1,shortSide/900);
            float cell = shortSide/18;
            float2 gridPosition = p/cell;
            // All panel edges use integer coordinates in the same centered square grid.
            // The lower strips are adjacent one-cell rows and share the side panels' baseline.
            float panelOuter = round(width/cell*.435);
            float panelInner = round(width/cell*.30);
            float panelBottom = round(height/cell*.38);
            float stripHalfWidth = max(1,round(width/cell*.235));

            // A sharp leading edge moves toward the bottom-right; only its trailing
            // side fades out. Antialias the edge over one output pixel. Both ends of
            // the eight-second loop are offscreen, so the phase wrap stays invisible.
            float sweepPosition = lerp(-.05,1.45,frac(seconds/8));
            float sweepCoordinate = (uv.x+uv.y)*.5;
            float behindEdge = sweepPosition-sweepCoordinate;
            float edgePixel = fwidth(sweepCoordinate);
            float sweep = smoothstep(-edgePixel*.5,edgePixel*.5,behindEdge)
                * (1-smoothstep(0,.40,behindEdge));
            float3 c = lerp(float3(.36,.36,.36),float3(.94,.94,.94),sweep);

            // Continuous hue/saturation ramp on the left, black-to-white ramp on the right.
            if (Inside(gridPosition,float4(-panelOuter,-panelBottom,-panelInner,panelBottom))) {
                float3 hue = Hue((gridPosition.y+panelBottom)/(2*panelBottom));
                c = lerp(float3(1,1,1),hue,saturate((gridPosition.x+panelOuter)/(panelOuter-panelInner)));
            }
            if (Inside(gridPosition,float4(panelInner,-panelBottom,panelOuter,panelBottom))) {
                float gray = saturate(gridPosition.y/(panelBottom*1.3)+.5);
                c = float3(gray,gray,gray);
            }

            float2 gridDistance = abs(frac(p/cell+.5)-.5)*cell;
            float grid = max(Line(gridDistance.x,stroke*.6),Line(gridDistance.y,stroke*.6));
            c = lerp(c,float3(.10,.10,.10),grid*.8);

            // Pixel-space distances keep the circle round at every canvas aspect ratio.
            float circle = Line(length(p)-shortSide*.40,stroke);
            float diagonal = min(abs(uv.x-uv.y),abs(uv.x+uv.y-1)) / length(1/size);
            float guides = max(circle,Line(diagonal,stroke*.65));
            float dashX = step(.5,frac((p.x+shortSide)/ (stroke*8)));
            float dashY = step(.5,frac((p.y+shortSide)/ (stroke*8)));
            guides = max(guides,max(Line(p.y,stroke)*dashX,Line(p.x,stroke)*dashY));
            c = lerp(c,float3(.9,.9,.9),guides);

            // Static detail wedge: period decreases from 12 to 2 output pixels.
            // Limit the frequency at the fine end to the output's Nyquist frequency.
            if (Inside(gridPosition,float4(-stripHalfWidth,panelBottom-2,stripHalfWidth,panelBottom-1))) {
                float x = (gridPosition.x+stripHalfWidth)*cell;
                float extent = 2*stripHalfWidth*cell;
                float phase = x/12 + (1.0/2-1.0/12)*x*x/(2*extent);
                float stripes = step(0,sin(phase*6.2831853));
                c = float3(stripes,stripes,stripes);
            }
            if (Inside(gridPosition,float4(-stripHalfWidth,panelBottom-1,stripHalfWidth,panelBottom))) {
                // One solid hue per grid cell, including when the canvas aspect ratio changes.
                float count = 2*stripHalfWidth;
                float band = min(count-1,floor(gridPosition.x+stripHalfWidth));
                c = lerp(Hue(band/count),float3(.10,.10,.10),grid*.8);
            }

            // Output dimensions, with a multiplication sign and space for five digits.
            float textScale = max(1,floor(shortSide/240));
            float2 text = (p-float2(-21.5*textScale,shortSide*.07))/textScale;
            float ink = Dimension(text,(uint)width);
            ink = max(ink,Dimension(text-float2(24,0),(uint)height));
            float2 cross = text-float2(20,1);
            if (all(cross >= 0) && all(cross < 3))
                ink = max(ink,(floor(cross.x)==floor(cross.y) || floor(cross.x)+floor(cross.y)==2) ? 1 : 0);
            c = lerp(c,float3(1,1,1),ink);

            float edge = min(min(uv.x,1-uv.x)*width,min(uv.y,1-uv.y)*height);
            c = lerp(c,float3(.9,.9,.9),1-smoothstep(stroke,stroke+1,edge));
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
