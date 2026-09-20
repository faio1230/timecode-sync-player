using System.Text;
namespace HapProbe;
internal static class Vectors
{
    public static void Emit(string root, string output)
    {
        var builder = new StringBuilder();
        builder.AppendLine("/* 自動生成（scripts/HapProbe --emit-vectors）。HAP の実データで解析と展開を固定する。 */");
        builder.AppendLine("#ifndef TCS_HAP_VECTORS_H");
        builder.AppendLine("#define TCS_HAP_VECTORS_H");
        builder.AppendLine("#include <stdint.h>");
        foreach (string directory in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(directory);
            string file = Directory.GetFiles(directory, "*.hap").OrderBy(f => f, StringComparer.Ordinal).First();
            byte[] data = File.ReadAllBytes(file);
            HapFrame frame = HapFrame.Parse(data);
            var destination = new byte[frame.DecompressedLength];
            frame.Decompress(destination, parallel: false);
            builder.AppendLine();
            builder.AppendLine($"/* {name}: 形式 0x{(int)frame.Format:X2} / かたまり {frame.Chunks.Count} 個 / 展開後 {frame.DecompressedLength} バイト */");
            builder.AppendLine($"static const int kTcsHapVector_{name}_format = 0x{(int)frame.Format:X2};");
            builder.AppendLine($"static const int kTcsHapVector_{name}_chunks = {frame.Chunks.Count};");
            builder.AppendLine($"static const uint32_t kTcsHapVector_{name}_decompressed = {frame.DecompressedLength};");
            builder.AppendLine($"static const uint64_t kTcsHapVector_{name}_hash = 0x{Fnv1a(destination):X16}ULL;");
            builder.Append($"static const uint8_t kTcsHapVector_{name}[] = {{");
            for (int i = 0; i < data.Length; i++)
            {
                if (i % 16 == 0) builder.AppendLine().Append("  ");
                builder.Append("0x").Append(data[i].ToString("X2")).Append(',');
            }
            builder.AppendLine();
            builder.AppendLine("};");
        }
        builder.AppendLine();
        builder.AppendLine("#endif");
        File.WriteAllText(output, builder.ToString());
        Console.WriteLine($"書き出し: {output}");
    }

    /* 展開結果の一致を見るための検査和（C++ 側も同じ式で計算する）。 */
    public static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte value in data) { hash ^= value; hash *= 1099511628211UL; }
        return hash;
    }
}
