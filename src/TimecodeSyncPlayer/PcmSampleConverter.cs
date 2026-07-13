using NAudio.Wave;

namespace TimecodeSyncPlayer;

internal static class PcmSampleConverter
{
    public static float[] ConvertToMonoFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        int frameBytes = bytesPerSample * format.Channels;
        if (bytesPerSample <= 0 || format.Channels <= 0 || frameBytes <= 0 || bytesRecorded <= 0)
            return [];

        int sampleCount = bytesRecorded / frameBytes;
        var samples = new float[sampleCount];
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;

        for (int i = 0; i < sampleCount; i++)
        {
            int offset = i * frameBytes;
            samples[i] = (bytesPerSample, isFloat) switch
            {
                (2, _) => BitConverter.ToInt16(buffer, offset) / 32768f,
                (3, _) => Read24Bit(buffer, offset) / 8388608f,
                (4, true) => BitConverter.ToSingle(buffer, offset),
                (4, false) => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => 0f
            };
        }

        return samples;
    }

    private static int Read24Bit(byte[] buffer, int offset)
    {
        int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        if ((value & 0x800000) != 0)
            value |= unchecked((int)0xFF000000);
        return value;
    }
}
