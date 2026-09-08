using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer;

internal static class PreviewFrameScaler
{
    internal const int MaximumWidth = 960;
    internal const int MaximumHeight = 540;
    internal const int MaximumBytes = MaximumWidth * MaximumHeight * 4;

    internal static (int Width, int Height) ValidateAndGetSize(IntPtr pixels, int width, int height, int stride)
    {
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0 ||
            (long)width * 4 > stride || (long)(height - 1) * stride + (long)width * 4 > int.MaxValue)
            throw new ArgumentException("Invalid Bgr32 preview source dimensions, pointer or stride.");
        if (width <= MaximumWidth && height <= MaximumHeight) return (width, height);
        if ((long)width * MaximumHeight >= (long)height * MaximumWidth)
            return (MaximumWidth, Math.Max(1, (int)((long)height * MaximumWidth / width)));
        return (Math.Max(1, (int)((long)width * MaximumHeight / height)), MaximumHeight);
    }

    // Reads only during this call. No source pointer, row or borrowed frame survives it.
    internal static void Copy(IntPtr source, int width, int height, int stride,
        byte[] destination, int targetWidth, int targetHeight)
    {
        if (width == targetWidth && height == targetHeight)
        {
            int rowBytes = width * 4;
            for (int y = 0; y < height; y++)
                Marshal.Copy(IntPtr.Add(source, y * stride), destination, y * rowBytes, rowBytes);
            return;
        }
        Span<int> target = MemoryMarshal.Cast<byte, int>(destination.AsSpan(0, targetWidth * targetHeight * 4));
        Span<int> sourceColumns = stackalloc int[targetWidth];
        for (int x = 0; x < targetWidth; x++) sourceColumns[x] = (int)((long)x * width / targetWidth) * 4;
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceRow = (int)((long)y * height / targetHeight) * stride;
            int targetRow = y * targetWidth;
            for (int x = 0; x < targetWidth; x++)
                target[targetRow + x] = Marshal.ReadInt32(source, sourceRow + sourceColumns[x]);
        }
    }
}
