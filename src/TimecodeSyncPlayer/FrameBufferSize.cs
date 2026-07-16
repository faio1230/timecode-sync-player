namespace TimecodeSyncPlayer;

internal static class FrameBufferSize
{
    private const int BytesPerPixel = 4;

    public static bool TryGetRequiredByteCount(int width, int height, out int requiredBytes)
    {
        requiredBytes = 0;
        if (width < 1 || height < 1)
            return false;

        try
        {
            long requiredBytesLong = checked((long)width * height * BytesPerPixel);
            if (requiredBytesLong > int.MaxValue)
                return false;

            requiredBytes = (int)requiredBytesLong;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static int GetRequiredByteCount(int width, int height)
    {
        if (TryGetRequiredByteCount(width, height, out int requiredBytes))
            return requiredBytes;

        throw new ArgumentOutOfRangeException(
            nameof(width),
            $"Invalid frame dimensions {width}x{height}: the required byte count must fit in Int32.");
    }
}
