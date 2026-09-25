using NAudio.Wave;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// LTC 波形をフレーム単位で逐次生成して WASAPI へ渡す。
///
/// <para>
/// 以前は全長ぶんを一度に <see cref="BufferedWaveProvider"/> へ積んでいたが、
/// バイト数の計算が int の桁あふれを起こし、4 時間の指定に対して実際には 54.4 分ぶんしか
/// 送出されないまま例外も出なかった（かつ 4 時間ぶんで float 配列が 5.5GB になる）。
/// ここでは 1 フレームずつ作るのでメモリは長さに依らず一定で、長さの上限も無い。
/// </para>
///
/// <para>
/// 送出できたフレーム数は <see cref="SentFrames"/> で数える。テストの最後に
/// <see cref="PlannedFrames"/> と突き合わせれば、途中で止まったことに気づける。
/// </para>
/// </summary>
internal sealed class LtcStreamingWaveProvider : IWaveProvider
{
    private readonly IEnumerator<LtcTimecode> _timecodes;
    private readonly LtcTestSignalGenerator.Encoder _encoder;
    private readonly int _channels;
    private readonly float[] _frameSamples;
    private byte[] _frameBytes;
    private int _frameByteCount;
    private int _frameBytePos;
    private bool _exhausted;
    private long _sentFrames;

    public LtcStreamingWaveProvider(
        IEnumerable<LtcTimecode> timecodes,
        double fps,
        int sampleRate,
        int channels,
        long plannedFrames,
        LtcTestSignalGenerator.Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(timecodes);
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _timecodes = timecodes.GetEnumerator();
        _encoder = new LtcTestSignalGenerator.Encoder(fps, sampleRate, options);
        _channels = channels;
        _frameSamples = new float[_encoder.MaxSamplesPerFrame];
        _frameBytes = new byte[_frameSamples.Length * channels * sizeof(float)];
        PlannedFrames = plannedFrames;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>流す予定のフレーム数。</summary>
    public long PlannedFrames { get; }

    /// <summary>実際に生成して渡したフレーム数。</summary>
    public long SentFrames => Interlocked.Read(ref _sentFrames);

    /// <summary>予定したフレームを全て渡し終えたか。</summary>
    public bool Completed => SentFrames >= PlannedFrames;

    public int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_frameBytePos >= _frameByteCount && !FillNextFrame())
            {
                // 尽きたら無音を返し続ける（従来の BufferedWaveProvider + ReadFully と同じ）。
                Array.Clear(buffer, offset + written, count - written);
                return count;
            }

            int chunk = Math.Min(count - written, _frameByteCount - _frameBytePos);
            Buffer.BlockCopy(_frameBytes, _frameBytePos, buffer, offset + written, chunk);
            _frameBytePos += chunk;
            written += chunk;
        }

        return written;
    }

    private bool FillNextFrame()
    {
        if (_exhausted)
            return false;

        if (!_timecodes.MoveNext())
        {
            _exhausted = true;
            return false;
        }

        int sampleCount = _encoder.Encode(
            LtcTestSignalGenerator.BuildFrameBits(_timecodes.Current), _frameSamples);

        int byteCount = sampleCount * _channels * sizeof(float);
        if (_frameBytes.Length < byteCount)
            _frameBytes = new byte[byteCount];

        if (_channels == 1)
        {
            Buffer.BlockCopy(_frameSamples, 0, _frameBytes, 0, sampleCount * sizeof(float));
        }
        else
        {
            // 1 フレームぶんだけなので、インターリーブの一時配列も 1 フレームぶんで足りる。
            Span<byte> destination = _frameBytes.AsSpan(0, byteCount);
            for (int frame = 0; frame < sampleCount; frame++)
            {
                float value = _frameSamples[frame];
                for (int channel = 0; channel < _channels; channel++)
                {
                    int at = ((frame * _channels) + channel) * sizeof(float);
                    BitConverter.TryWriteBytes(destination[at..], value);
                }
            }
        }

        _frameByteCount = byteCount;
        _frameBytePos = 0;
        Interlocked.Increment(ref _sentFrames);
        return true;
    }
}
