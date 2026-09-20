using System.IO.Compression;

namespace HapProbe;

/// <summary>確認用の最小の PNG 書き出し（BGRA のバイト列をそのまま保存する）。</summary>
internal static class Png
{
    public static void Write(string path, int width, int height, byte[] bgra)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;     // ビット深度
        header[9] = 6;     // RGBA
        WriteChunk(stream, "IHDR", header);

        // 各行の先頭にフィルタ種別 0 を置き、BGRA を RGBA に並べ替える。
        var raw = new byte[(width * 4 + 1) * height];
        int cursor = 0;
        for (int y = 0; y < height; y++)
        {
            raw[cursor++] = 0;
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                raw[cursor++] = bgra[row + x * 4 + 2];
                raw[cursor++] = bgra[row + x * 4 + 1];
                raw[cursor++] = bgra[row + x * 4 + 0];
                raw[cursor++] = 255;
            }
        }
        using var compressed = new MemoryStream();
        compressed.Write([0x78, 0x01]);   // zlib ヘッダ
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        WriteBigEndianAdler(compressed, raw);
        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteBigEndianAdler(Stream stream, byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        uint adler = (b << 16) | a;
        stream.Write([(byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler]);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc32(typeBytes, data);
        stream.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int k = 0; k < 8; k++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in type) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
