using FluentAssertions;
using NAudio.Wave;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 逐次生成の LTC 波形が、全長を一度に作る経路と 1 サンプルも変わらないこと、
/// 長時間でも途中で切れないことを確かめる。
///
/// <para>
/// 4 時間の連続追従試験で、LTC が 54.4 分で静かに止まっていた
/// （<c>interleavedSamples.Length * sizeof(float)</c> が int で桁あふれし、
/// 5,549,568,000 バイトが 1,254,600,704 バイトに巻き込んでいた）。
/// 例外も警告も出ないため、試験は「4 時間流した」つもりのまま失敗の理由を取り違えた。
/// </para>
/// </summary>
public class LtcStreamingSignalTests
{
    [Theory]
    [InlineData(24, 48000, 1)]
    [InlineData(25, 48000, 2)]
    [InlineData(30, 44100, 2)]
    [InlineData(30000.0 / 1001.0, 48000, 2)]
    public void Encoder_ProducesTheSameWaveformAsTheWholeArrayPath(double fps, int sampleRate, int channels)
    {
        var start = new LtcTimecode(1, 2, 3, 4, false);
        int nominalFps = Math.Abs(fps - (30000.0 / 1001.0)) < 0.01 ? 30 : (int)Math.Round(fps);
        IReadOnlyList<LtcTimecode> timecodes =
            LtcSignalPlayer.BuildContinuousTimecodes(start, fps, nominalFps * 3);
        float[] expected = LtcTestSignalGenerator.Generate(timecodes, fps, sampleRate);

        var provider = new LtcStreamingWaveProvider(
            timecodes, fps, sampleRate, channels, timecodes.Count);
        float[] actual = ReadAllChannel0(provider, expected.Length, channels);

        actual.Should().Equal(expected, "逐次生成でも極性・遷移位置・フレーム境界は変わらない");
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(30)]
    public void StreamedSignal_DecodesBackToTheSourceTimecodes(int fps)
    {
        var start = new LtcTimecode(0, 7, 20, 0, false);
        IReadOnlyList<LtcTimecode> timecodes = LtcSignalPlayer.BuildContinuousTimecodes(start, fps, fps * 2);
        var provider = new LtcStreamingWaveProvider(timecodes, fps, 48000, 2, timecodes.Count);
        float[] mono = ReadAllChannel0(provider, 48000 * 2, 2);

        var decoder = new LtcDecoder(48000, fps);
        decoder.Write(mono, mono.Length);
        var decoded = new List<LtcTimecode>();
        while (decoder.Read() is { } frame) decoded.Add(frame.Timecode);

        decoded.Should().HaveCountGreaterThanOrEqualTo(fps);
        decoded.Should().BeSubsetOf(timecodes);
        decoded.Select(tc => (tc.Seconds * fps) + tc.Frames)
            .Should().BeInAscendingOrder("取りこぼしても戻りはしない");
    }

    [Fact]
    public void StreamedSignal_KeepsGeneratingBeyondTheOldIntegerOverflowLimit()
    {
        // 旧経路が黙って切り捨てていた境界（48kHz / 2ch / float で 3266.7 秒）を超えて
        // 同じ波形が出続けることを、桁あふれしていた位置の前後で確かめる。
        const int SampleRate = 48000;
        const int Fps = 25;
        const long OverflowFrame = 3266L * Fps;
        var start = new LtcTimecode(0, 0, 0, 0, false);

        var provider = new LtcStreamingWaveProvider(
            LongRun(start, Fps, OverflowFrame + 50), Fps, SampleRate, 2, OverflowFrame + 50);

        var buffer = new byte[SampleRate * 2 * sizeof(float)];
        long frames = 0;
        while (provider.SentFrames < OverflowFrame + 50)
        {
            provider.Read(buffer, 0, buffer.Length);
            if (++frames > OverflowFrame + 200) break; // 念のための空回り防止
        }

        provider.SentFrames.Should().Be(OverflowFrame + 50);
        provider.Completed.Should().BeTrue();
    }

    [Fact]
    public void StreamingProvider_ReportsShortfallSoAStalledSignalIsVisible()
    {
        var timecodes = LtcSignalPlayer.BuildContinuousTimecodes(new LtcTimecode(0, 0, 0, 0, false), 25, 10);
        var provider = new LtcStreamingWaveProvider(timecodes, 25, 48000, 2, plannedFrames: 100);

        var buffer = new byte[48000 * 2 * sizeof(float)];
        provider.Read(buffer, 0, buffer.Length);

        provider.SentFrames.Should().Be(10);
        provider.Completed.Should().BeFalse("予定より短ければ Completed が false になる");
    }

    private static IEnumerable<LtcTimecode> LongRun(LtcTimecode start, int fps, long count)
    {
        LtcTimecode current = start;
        for (long i = 0; i < count; i++)
        {
            yield return current;
            current = LtcTestSignalGenerator.Increment(current, fps);
        }
    }

    private static float[] ReadAllChannel0(IWaveProvider provider, int sampleCount, int channels)
    {
        var bytes = new byte[sampleCount * channels * sizeof(float)];
        int read = 0;
        while (read < bytes.Length)
        {
            int got = provider.Read(bytes, read, bytes.Length - read);
            if (got <= 0) break;
            read += got;
        }

        var mono = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            mono[i] = BitConverter.ToSingle(bytes, i * channels * sizeof(float));
        return mono;
    }
}
