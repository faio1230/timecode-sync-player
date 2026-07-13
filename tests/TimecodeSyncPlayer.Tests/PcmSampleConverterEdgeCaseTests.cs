using FluentAssertions;
using NAudio.Wave;

namespace TimecodeSyncPlayer.Tests;

public class PcmSampleConverterEdgeCaseTests
{
    [Fact]
    public void ConvertToMonoFloat_EightBitPcm_FallsThroughToZero()
    {
        byte[] buffer = [0x80, 0x7F, 0x00, 0xFF];
        var format = new WaveFormat(48_000, 8, 1);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(4);
        samples.Should().AllBeEquivalentTo(0f);
    }

    [Fact]
    public void ConvertToMonoFloat_TwentyFourBitPcm_ReadsCorrectValues()
    {
        byte[] buffer =
        [
            0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F,
            0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80,
        ];
        var format = new WaveFormat(48_000, 24, 2);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(2);
        samples[0].Should().BeApproximately(-1f, 0.001f);
        samples[1].Should().BeApproximately(1f, 0.001f);
    }

    [Fact]
    public void ConvertToMonoFloat_ThirtyTwoBitPcmInteger_ReadsCorrectValues()
    {
        byte[] buffer = new byte[16];
        BitConverter.GetBytes(1073741824).CopyTo(buffer, 0);
        BitConverter.GetBytes(0).CopyTo(buffer, 4);
        BitConverter.GetBytes(-1073741824).CopyTo(buffer, 8);
        BitConverter.GetBytes(0).CopyTo(buffer, 12);
        var format = new WaveFormat(48_000, 32, 2);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(2);
        samples[0].Should().BeApproximately(0.5f, 0.001f);
        samples[1].Should().BeApproximately(-0.5f, 0.001f);
    }

    [Fact]
    public void ConvertToMonoFloat_MonoInput_ReturnsAllSamples()
    {
        byte[] buffer =
        [
            0x00, 0x40, 0x00, 0xC0, 0x00, 0x20, 0x00, 0xE0,
        ];
        var format = new WaveFormat(48_000, 16, 1);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(4);
        samples[0].Should().BeApproximately(0.5f, 0.0001f);
        samples[1].Should().BeApproximately(-0.5f, 0.0001f);
        samples[2].Should().BeApproximately(0.25f, 0.0001f);
        samples[3].Should().BeApproximately(-0.25f, 0.0001f);
    }

    [Fact]
    public void ConvertToMonoFloat_UnalignedBuffer_TruncatesRemainder()
    {
        byte[] buffer =
        [
            0x00, 0x40, 0x00, 0x20, 0x00,
        ];
        var format = new WaveFormat(48_000, 16, 2);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(1);
        samples[0].Should().BeApproximately(0.5f, 0.0001f);
    }

    [Fact]
    public void ConvertToMonoFloat_EmptyBuffer_ReturnsEmptyArray()
    {
        byte[] buffer = [];
        var format = new WaveFormat(48_000, 16, 2);

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, 0, format);

        samples.Should().BeEmpty();
    }

    [Fact]
    public void ConvertToMonoFloat_AllZeroSamples_ReturnsZeros()
    {
        byte[] buffer = new byte[8];
        var format = new WaveFormat(48_000, 16, 2);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(2);
        samples.Should().AllBeEquivalentTo(0f);
    }

    [Fact]
    public void ConvertToMonoFloat_Max16BitValue_ReturnsNearOne()
    {
        byte[] buffer = [0xFF, 0x7F, 0x00, 0x00];
        var format = new WaveFormat(48_000, 16, 2);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(1);
        samples[0].Should().BeApproximately(1f, 0.0001f);
    }

    [Fact]
    public void ConvertToMonoFloat_Min16BitValue_ReturnsNegativeOne()
    {
        byte[] buffer = [0x00, 0x80, 0x00, 0x00];
        var format = new WaveFormat(48_000, 16, 2);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(1);
        samples[0].Should().BeApproximately(-1f, 0.0001f);
    }

    [Fact]
    public void ConvertToMonoFloat_NegativeSigned16BitValues_AreCorrect()
    {
        byte[] buffer =
        [
            0x00, 0x80, 0x00, 0x00,
            0x00, 0xC0, 0x00, 0x00,
            0xFF, 0xFF, 0x00, 0x00,
        ];
        var format = new WaveFormat(48_000, 16, 2);
        int bytesRecorded = buffer.Length;

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, bytesRecorded, format);

        samples.Should().HaveCount(3);
        samples[0].Should().BeApproximately(-1f, 0.0001f);
        samples[1].Should().BeApproximately(-0.5f, 0.0001f);
        samples[2].Should().BeApproximately(-0.00003f, 0.0001f);
    }

    [Fact]
    public void ConvertToMonoFloat_ZeroBytesPerSample_ReturnsEmpty()
    {
        byte[] buffer = [0x01, 0x02, 0x03, 0x04];
        var format = new WaveFormat(48_000, 0, 2);

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, buffer.Length, format);

        samples.Should().BeEmpty();
    }

    [Fact]
    public void ConvertToMonoFloat_ZeroBytesRecorded_ReturnsEmpty()
    {
        byte[] buffer = [0x01, 0x02, 0x03, 0x04];
        var format = new WaveFormat(48_000, 16, 2);

        float[] samples = PcmSampleConverter.ConvertToMonoFloat(buffer, 0, format);

        samples.Should().BeEmpty();
    }
}
