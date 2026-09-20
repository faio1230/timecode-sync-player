using System.Text;
using Vortice.D3DCompiler;

namespace HapProbe;

/// <summary>
/// 試作で絵の正しさを確かめたシェーダを、変換済みのバイト列として C のヘッダに書き出す。
/// shim 側はこれを埋め込むので、実行時に d3dcompiler を読み込む必要が無い（依存を増やさない）。
/// </summary>
internal static class ShaderEmitter
{
    public static void Emit(string output)
    {
        var builder = new StringBuilder();
        builder.AppendLine("/* 自動生成（scripts/HapProbe --emit-shaders）。HAP の BC テクスチャを BGRA へ展開する。");
        builder.AppendLine(" * 元の HLSL は scripts/HapProbe/Gpu.cs の Shaders.Source。試作で参照画像との一致を確認済み。 */");
        builder.AppendLine("#ifndef TCS_HAP_SHADERS_H");
        builder.AppendLine("#define TCS_HAP_SHADERS_H");
        builder.AppendLine("#include <stdint.h>");
        Append(builder, "kTcsHapVertexShader", "VsMain", "vs_5_0");
        Append(builder, "kTcsHapPixelShaderPlain", Shaders.PsPlain, "ps_5_0");
        Append(builder, "kTcsHapPixelShaderYCoCg", Shaders.PsYCoCg, "ps_5_0");
        builder.AppendLine();
        builder.AppendLine("#endif");
        File.WriteAllText(output, builder.ToString());
        Console.WriteLine($"書き出し: {output}");
    }

    private static void Append(StringBuilder builder, string name, string entry, string profile)
    {
        ReadOnlyMemory<byte> blob = Compiler.Compile(Shaders.Source, entry, "hap", profile);
        ReadOnlySpan<byte> bytes = blob.Span;
        builder.AppendLine();
        builder.AppendLine($"/* {entry} ({profile}) */");
        builder.Append($"static const uint8_t {name}[] = {{");
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i % 16 == 0) builder.AppendLine().Append("  ");
            builder.Append("0x").Append(bytes[i].ToString("X2")).Append(',');
        }
        builder.AppendLine();
        builder.AppendLine("};");
    }
}
