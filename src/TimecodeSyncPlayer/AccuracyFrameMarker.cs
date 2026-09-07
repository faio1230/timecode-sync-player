using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer;

internal readonly record struct AccuracyFrameProbe(bool MarkerValid, int? ClipId, int? FrameIndex, bool IsBlack);

/// <summary>Measurement-only sparse probe of the published BGR32 pixels, never playback state.</summary>
internal static class AccuracyFrameMarker
{
    public static AccuracyFrameProbe Probe(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (!ValidLayout(width, height, stride, out int length) || pixels.Length < length) return default;
        return Probe(new PixelSource(pixels, IntPtr.Zero, stride), width, height);
    }

    // The caller owns the bitmap and keeps its back buffer alive throughout this synchronous probe.
    public static AccuracyFrameProbe Probe(IntPtr pixels, int width, int height, int stride)
    {
        if (pixels == IntPtr.Zero || !ValidLayout(width, height, stride, out _)) return default;
        return Probe(new PixelSource(default, pixels, stride), width, height);
    }

    private static bool ValidLayout(int width, int height, int stride, out int length)
    {
        long bytes = (long)stride * height;
        length = 0;
        if (width <= 0 || height <= 0 || stride < (long)width * 4 || bytes > int.MaxValue) return false;
        length = (int)bytes;
        return true;
    }

    private static AccuracyFrameProbe Probe(PixelSource pixels, int width, int height)
    {
        bool isBlack = true;
        // Includes all corners and center; missing marker alone never establishes black output.
        for (int row = 0; row < 9; row++)
            for (int col = 0; col < 9; col++)
                isBlack &= pixels.Classify((int)((long)(width - 1) * col / 8), (int)((long)(height - 1) * row / 8)) == 0;

        if (width != 1920 || height != 1080) return new(false, null, null, isBlack);

        Span<byte> marker = stackalloc byte[6];
        marker.Clear();
        bool valid = true;
        for (int bit = 0; bit < 48; bit++)
        {
            int level = pixels.Classify(32 + bit * 16 + 8, 32 + 16);
            isBlack &= level == 0;
            if (level < 0) valid = false;
            else marker[bit / 8] |= (byte)(level << (7 - bit % 8));
        }
        valid &= marker[0] == 0xDD && marker[1] == 0xAA && marker[4] is >= 1 and <= 3;
        valid &= (marker[0] ^ marker[1] ^ marker[2] ^ marker[3] ^ marker[4]) == marker[5];
        return valid
            ? new(true, marker[4], (marker[2] << 8) | marker[3], false)
            : new(false, null, null, isBlack);
    }

    private readonly ref struct PixelSource(ReadOnlySpan<byte> bytes, IntPtr pointer, int stride)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;

        public int Classify(int x, int y)
        {
            int index = y * stride + x * 4;
            byte b = Read(index), g = Read(index + 1), r = Read(index + 2);
            if (b >= 192 && g >= 192 && r >= 192) return 1;
            if (b <= 64 && g <= 64 && r <= 64) return 0;
            return -1;
        }

        private byte Read(int index) => pointer == IntPtr.Zero ? _bytes[index] : Marshal.ReadByte(pointer, index);
    }
}
