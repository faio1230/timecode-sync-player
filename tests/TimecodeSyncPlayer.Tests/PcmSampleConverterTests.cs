using FluentAssertions;
using NAudio.Wave;

namespace TimecodeSyncPlayer.Tests;

public class PcmSampleConverterTests
{
    [Fact]
    public void ConvertToMonoFloat_ReadsFirstChannelFrom16BitPcm()
    {
        byte[] buffer =
        [
            0x00, 0x40, 0x00, 0x20,
            0x00, 0xC0, 0x00, 0x10,
        ];
        var format = new WaveFormat(48_000, 16, 2);

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, buffer.Length, format);

        samples.Should().HaveCount(2);
        samples[0].Should().BeApproximately(0.5f, 0.0001f);
        samples[1].Should().BeApproximately(-0.5f, 0.0001f);
    }

    [Fact]
    public void ConvertToMonoFloat_ReadsFirstChannelFrom32BitFloat()
    {
        byte[] buffer = new byte[16];
        BitConverter.GetBytes(0.25f).CopyTo(buffer, 0);
        BitConverter.GetBytes(0.75f).CopyTo(buffer, 4);
        BitConverter.GetBytes(-0.25f).CopyTo(buffer, 8);
        BitConverter.GetBytes(-0.75f).CopyTo(buffer, 12);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, buffer.Length, format);

        samples.Should().Equal(0.25f, -0.25f);
    }
}
