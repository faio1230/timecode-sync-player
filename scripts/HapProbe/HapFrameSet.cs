using System.Buffers.Binary;
using Snappier;

namespace HapProbe;

/// <summary>HAP のテクスチャ形式（セクション種別の下位ニブル）。</summary>
internal enum HapTextureFormat
{
    Dxt1 = 0x0B,        // Hap（RGB_DXT1、1 ブロック 8 バイト）
    Dxt5 = 0x0E,        // Hap Alpha（RGBA_DXT5）
    YCoCgDxt5 = 0x0F,   // Hap Q（Scaled YCoCg_DXT5、1 ブロック 16 バイト）
    Rgtc1 = 0x01,       // Hap Alpha Only
    Bc7 = 0x0C,         // Hap 7
    Bc6h = 0x0D,        // Hap HDR
}

/// <summary>1 コマぶんの「かたまり」（並列に展開できる単位）。</summary>
internal readonly record struct HapChunk(int Compressor, int Offset, int Length, int OutputOffset, int OutputLength);

/// <summary>
/// HAP の 1 コマ。ヘッダは 4 バイト（サイズ 3 + 種別 1）で、サイズ 0 なら 8 バイト形式。
/// 種別の上位ニブルが二段目の圧縮（0xA0=無し / 0xB0=Snappy / 0xC0=かたまり分割）。
/// 仕様: https://github.com/Vidvox/hap/blob/master/documentation/HapVideoDRAFT.md
/// </summary>
internal sealed class HapFrame
{
    public required byte[] Data { get; init; }              // ファイルの中身そのまま
    public required HapTextureFormat Format { get; init; }
    public required int DecompressedLength { get; init; }   // BC データの大きさ（= 展開後）
    public required IReadOnlyList<HapChunk> Chunks { get; init; }

    public bool IsChunked => Chunks.Count > 1;

    /// <summary>このコマを <paramref name="destination"/> へ展開する。並列にするかは呼び出し側。</summary>
    public void Decompress(byte[] destination, bool parallel)
    {
        if (destination.Length < DecompressedLength)
            throw new ArgumentException($"展開先が小さい: {destination.Length} < {DecompressedLength}");
        if (!parallel || Chunks.Count == 1)
        {
            foreach (HapChunk chunk in Chunks) DecompressChunkStatic(Data, chunk, destination);
            return;
        }
        // かたまりごとに独立して展開できる（出力の範囲が重ならないので、書き込みの競合は無い）。
        byte[] data = Data;
        IReadOnlyList<HapChunk> chunks = Chunks;
        Parallel.For(0, chunks.Count, i => DecompressChunkStatic(data, chunks[i], destination));
    }

    private static void DecompressChunkStatic(byte[] data, in HapChunk chunk, byte[] destination)
    {
        ReadOnlySpan<byte> source = data.AsSpan(chunk.Offset, chunk.Length);
        Span<byte> target = destination.AsSpan(chunk.OutputOffset, chunk.OutputLength);
        switch (chunk.Compressor)
        {
            case 0xA: source.CopyTo(target); break;
            case 0xB: Snappy.Decompress(source, target); break;
            default: throw new NotSupportedException($"未知の二段目圧縮: 0x{chunk.Compressor:X}");
        }
    }

    public static HapFrame Parse(byte[] data)
    {
        (int sectionLength, int sectionType, int headerLength) = ReadSectionHeader(data, 0);
        int compressor = (sectionType & 0xF0) >> 4;
        var format = (HapTextureFormat)(sectionType & 0x0F);
        int body = headerLength;

        if (compressor is 0xA or 0xB)
        {
            int decompressed = compressor == 0xA
                ? sectionLength
                : (int)Snappy.GetUncompressedLength(data.AsSpan(body, sectionLength));
            return new HapFrame
            {
                Data = data,
                Format = format,
                DecompressedLength = decompressed,
                Chunks = new[] { new HapChunk(compressor, body, sectionLength, 0, decompressed) },
            };
        }
        if (compressor != 0xC)
            throw new NotSupportedException($"未知の二段目圧縮: 0x{compressor:X}");

        // 0xC: 先頭に「展開の手順（Decode Instructions Container、種別 0x01）」が入る。
        (int instructionsLength, int instructionsType, int instructionsHeader) = ReadSectionHeader(data, body);
        if ((instructionsType & 0x0F) != 0x01)
            throw new NotSupportedException($"展開手順のセクションが見つからない: 0x{instructionsType:X}");
        int cursor = body + instructionsHeader;
        int instructionsEnd = cursor + instructionsLength;
        int[]? compressors = null;
        int[]? compressedSizes = null;
        int[]? uncompressedSizes = null;
        while (cursor < instructionsEnd)
        {
            (int length, int type, int header) = ReadSectionHeader(data, cursor);
            int payload = cursor + header;
            switch (type & 0x0F)
            {
                case 0x02: // Chunk Second-Stage Compressor Table（1 かたまり 1 バイト）
                    compressors = new int[length];
                    for (int i = 0; i < length; i++) compressors[i] = data[payload + i];
                    break;
                case 0x03: // Chunk Size Table（1 かたまり 4 バイト、圧縮後）
                    compressedSizes = ReadUInt32Table(data, payload, length);
                    break;
                case 0x04: // Chunk Offset Table（使わない。サイズから積む）
                    break;
                case 0x05: // Uncompressed Chunk Size Table
                    uncompressedSizes = ReadUInt32Table(data, payload, length);
                    break;
            }
            cursor = payload + length;
        }
        if (compressors is null || compressedSizes is null)
            throw new NotSupportedException("かたまりの表が足りない");

        var chunks = new List<HapChunk>(compressors.Length);
        int dataOffset = instructionsEnd;
        int outputOffset = 0;
        for (int i = 0; i < compressors.Length; i++)
        {
            int compressedSize = compressedSizes[i];
            int uncompressed = uncompressedSizes is not null
                ? uncompressedSizes[i]
                : compressors[i] == 0xB
                    ? (int)Snappy.GetUncompressedLength(data.AsSpan(dataOffset, compressedSize))
                    : compressedSize;
            chunks.Add(new HapChunk(compressors[i], dataOffset, compressedSize, outputOffset, uncompressed));
            dataOffset += compressedSize;
            outputOffset += uncompressed;
        }
        return new HapFrame { Data = data, Format = format, DecompressedLength = outputOffset, Chunks = chunks };
    }

    private static int[] ReadUInt32Table(byte[] data, int offset, int length)
    {
        int count = length / 4;
        var values = new int[count];
        for (int i = 0; i < count; i++)
            values[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + i * 4, 4));
        return values;
    }

    /// <summary>セクションヘッダ（長さ・種別・ヘッダ自身の大きさ）。</summary>
    private static (int Length, int Type, int HeaderLength) ReadSectionHeader(byte[] data, int offset)
    {
        int length = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        int type = data[offset + 3];
        if (length != 0) return (length, type, 4);
        // 長さ 0 は「8 バイトヘッダ」の印。続く 4 バイトが本当の長さ。
        int longLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
        return (longLength, type, 8);
    }
}

/// <summary>切り出した 1 コマ 1 ファイルの束を読み込んで、メモリ上で周回させる。</summary>
internal sealed class HapFrameSet
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required IReadOnlyList<HapFrame> Frames { get; init; }

    public HapTextureFormat Format => Frames[0].Format;
    public int DecompressedLength => Frames[0].DecompressedLength;
    public long TotalCompressedBytes => Frames.Sum(f => (long)f.Data.Length);

    public static HapFrameSet Load(string directory, string name, int width, int height)
    {
        var files = Directory.GetFiles(directory, "*.hap").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new InvalidOperationException($"コマが無い: {directory}");
        var frames = files.Select(f => HapFrame.Parse(File.ReadAllBytes(f))).ToArray();
        return new HapFrameSet { Name = name, Width = width, Height = height, Frames = frames };
    }
}
