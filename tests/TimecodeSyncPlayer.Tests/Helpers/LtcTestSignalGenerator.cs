using System;
using System.Collections.Generic;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// テスト用の LTC（Linear Timecode）バイフェーズマーク（Manchester / BMC）変調波形生成器。
///
/// <para>
/// 波形仕様は <c>src/TimecodeSyncPlayer/LtcDecoder.cs</c> のデコード実装を正としている:
/// </para>
/// <list type="bullet">
///   <item>1フレーム = 80ビット（SMPTE 12M）。ビットは LSB ファースト（フレーム bit0 が最初）。</item>
///   <item>各ビット境界に必ず遷移がある。ビット "1" はさらにビット中央にも遷移を持つ。</item>
///   <item>デコーダはゼロクロッシングで遷移を検出し、ショート2本=「1」/ロング1本=「0」で復元する。</item>
///   <item>同期ワード（frame bit64〜79）は受信順 <c>0011111111111101</c>（= デコーダの 0xBFFC）。</item>
/// </list>
/// <para>
/// 振幅・極性反転・DCオフセット・加算ノイズをオプションで指定できる。矩形波なので
/// 振幅と極性はゼロクロッシング判定に影響せず、デコード結果は変わらない（BMCの極性無依存性）。
/// </para>
/// </summary>
internal static class LtcTestSignalGenerator
{
    public sealed class Options
    {
        /// <summary>矩形波の振幅（絶対値）。</summary>
        public float Amplitude { get; init; } = 1.0f;

        /// <summary>true で波形全体を極性反転する（BMCは極性無依存のはず）。</summary>
        public bool Invert { get; init; }

        /// <summary>
        /// 全サンプルに加える DC オフセット。
        /// 振幅（<see cref="Amplitude"/>）未満であればゼロクロッシングは維持されデコード可能。
        /// 振幅以上にするとゼロクロッシングが消失し、デコード不能になる点に注意。
        /// </summary>
        public float DcOffset { get; init; }

        /// <summary>加算する一様乱数ノイズの振幅（絶対値）。0 でノイズなし。</summary>
        public double NoiseAmplitude { get; init; }

        /// <summary>ノイズ用の固定乱数シード（再現性のため）。</summary>
        public int NoiseSeed { get; init; } = 12345;
    }

    /// <summary>
    /// 1個の <see cref="LtcTimecode"/> を 80ビットの LTC フレームビット列に変換する。
    /// 配列 index = フレームビット番号（bit0 が LSB ファーストで最初に送出される）。
    /// </summary>
    public static bool[] BuildFrameBits(LtcTimecode tc)
    {
        var bits = new bool[80];

        SetBits(bits, 0, 4, tc.Frames % 10);   // frame units
        SetBits(bits, 8, 2, tc.Frames / 10);   // frame tens
        bits[10] = tc.DropFrame;               // drop frame flag

        SetBits(bits, 16, 4, tc.Seconds % 10); // sec units
        SetBits(bits, 24, 3, tc.Seconds / 10); // sec tens

        SetBits(bits, 32, 4, tc.Minutes % 10); // min units
        SetBits(bits, 40, 3, tc.Minutes / 10); // min tens

        SetBits(bits, 48, 4, tc.Hours % 10);   // hour units
        SetBits(bits, 56, 2, tc.Hours / 10);   // hour tens

        // 同期ワード（受信順）: 0 0 1 1 1 1 1 1 1 1 1 1 1 1 0 1
        // = デコーダの FwdSync (0xBFFC)
        int[] sync = { 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1 };
        for (int i = 0; i < 16; i++)
            bits[64 + i] = sync[i] != 0;

        return bits;
    }

    /// <summary>単一タイムコードの波形を生成する。</summary>
    public static float[] Generate(LtcTimecode tc, double fps, int sampleRate, Options? options = null)
        => Generate(new[] { tc }, fps, sampleRate, options);

    /// <summary>連続するタイムコード列を1本の連続波形として生成する。</summary>
    /// <remarks>
    /// 全長ぶんを一度に確保するため、長時間の送出には使わない（4 時間ぶんで float 配列が
    /// 5.5GB になる）。逐次生成は <see cref="Encoder"/> を使う。
    /// </remarks>
    public static float[] Generate(IEnumerable<LtcTimecode> timecodes, double fps, int sampleRate, Options? options = null)
    {
        var frames = new List<bool[]>();
        foreach (var tc in timecodes)
            frames.Add(BuildFrameBits(tc));
        return EncodeFrames(frames, fps, sampleRate, options);
    }

    /// <summary>
    /// フレームを 1 つずつ波形へ変換する逐次エンコーダ。
    ///
    /// <para>
    /// 全長ぶんの配列を確保せずに、<see cref="Generate(IEnumerable{LtcTimecode}, double, int, Options?)"/>
    /// と 1 サンプルも違わない波形を作る。遷移位置はフレーム内の相対ではなく通し
    /// （<see cref="_globalBit"/>）で計算し、極性・ノイズ乱数・フレーム境界をまたぐ遷移を
    /// インスタンスに持ち越すため、フレーム単位に切っても波形は変わらない。
    /// </para>
    /// </summary>
    public sealed class Encoder
    {
        private readonly double _samplesPerBit;
        private readonly Options _options;
        private readonly Random? _rng;
        // フレーム境界をまたいで効く遷移があるため（最後のビットの中央遷移は次フレームの
        // 先頭サンプルで初めて n を超える）、消費位置ごと持ち越す。
        private readonly List<double> _pending = new(176);
        private int _cursor;
        private long _globalBit;
        private long _sampleIndex;
        private float _level;

        public Encoder(double fps, int sampleRate, Options? options = null)
        {
            if (!(fps > 0)) throw new ArgumentOutOfRangeException(nameof(fps));
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            _options = options ?? new Options();
            _samplesPerBit = sampleRate / (fps * 80.0);
            _rng = _options.NoiseAmplitude > 0 ? new Random(_options.NoiseSeed) : null;
            _level = _options.Amplitude; // 開始レベル（position 0 の境界遷移で反転する）
        }

        /// <summary>1 フレームが占めるサンプル数の上限（受け皿の確保に使う）。</summary>
        public int MaxSamplesPerFrame => (int)Math.Ceiling(_samplesPerBit * 80.0) + 2;

        /// <summary>これまでに書き出したサンプル数（通し）。</summary>
        public long SampleCount => _sampleIndex;

        /// <summary>1 フレームぶんの波形を書き、書いた長さを返す。</summary>
        public int Encode(bool[] frameBits, Span<float> destination)
        {
            ArgumentNullException.ThrowIfNull(frameBits);
            if (frameBits.Length != 80)
                throw new ArgumentException("LTC フレームは 80 ビット。", nameof(frameBits));

            long endSample = (long)Math.Round((_globalBit + 80) * _samplesPerBit);
            int count = (int)(endSample - _sampleIndex);
            if (destination.Length < count)
                throw new ArgumentException($"受け皿が {count} サンプルに足りない。", nameof(destination));

            if (_cursor > 0)
            {
                _pending.RemoveRange(0, _cursor);
                _cursor = 0;
            }

            for (int b = 0; b < 80; b++)
            {
                double boundary = (_globalBit + b) * _samplesPerBit;
                _pending.Add(boundary);                                  // 境界遷移（全ビット共通）
                if (frameBits[b])
                    _pending.Add(boundary + _samplesPerBit * 0.5);       // "1" の中央遷移
            }

            for (int i = 0; i < count; i++)
            {
                long n = _sampleIndex + i;
                while (_cursor < _pending.Count && _pending[_cursor] <= n)
                {
                    _level = -_level;
                    _cursor++;
                }

                float value = _level;
                if (_options.Invert) value = -value;
                value += _options.DcOffset;
                if (_rng != null)
                    value += (float)((_rng.NextDouble() * 2.0 - 1.0) * _options.NoiseAmplitude);

                destination[i] = value;
            }

            _globalBit += 80;
            _sampleIndex = endSample;
            return count;
        }
    }

    /// <summary>
    /// 非ドロップフレームのタイムコードを1フレーム進める。連続フレームテスト用。
    /// </summary>
    public static LtcTimecode Increment(LtcTimecode tc, int fps)
    {
        int f = tc.Frames + 1;
        int s = tc.Seconds, m = tc.Minutes, h = tc.Hours;
        if (f >= fps) { f = 0; s++; }
        if (s >= 60) { s = 0; m++; }
        if (m >= 60) { m = 0; h++; }
        if (h >= 24) { h = 0; }
        return tc with { Frames = f, Seconds = s, Minutes = m, Hours = h };
    }

    // ── 内部実装 ─────────────────────────────────────────────────

    private static float[] EncodeFrames(List<bool[]> frames, double fps, int sampleRate, Options? options)
    {
        double samplesPerBit = sampleRate / (fps * 80.0);
        long totalSamples = (long)Math.Round(frames.Count * 80L * samplesPerBit);
        if (totalSamples > int.MaxValue / 2)
            throw new ArgumentOutOfRangeException(nameof(frames),
                $"波形が長すぎる（{totalSamples} サンプル）。長時間の送出は Encoder で逐次生成する。");

        var samples = new float[totalSamples];
        var encoder = new Encoder(fps, sampleRate, options);
        int offset = 0;
        foreach (bool[] frame in frames)
            offset += encoder.Encode(frame, samples.AsSpan(offset));

        return samples;
    }

    private static void SetBits(bool[] bits, int start, int count, int value)
    {
        for (int i = 0; i < count; i++)
            bits[start + i] = ((value >> i) & 1) != 0;
    }
}
