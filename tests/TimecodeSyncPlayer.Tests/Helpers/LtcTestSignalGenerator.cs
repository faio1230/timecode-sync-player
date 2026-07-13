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

        /// <summary>全サンプルに加える DC オフセット。</summary>
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
    public static float[] Generate(IEnumerable<LtcTimecode> timecodes, double fps, int sampleRate, Options? options = null)
    {
        var frames = new List<bool[]>();
        foreach (var tc in timecodes)
            frames.Add(BuildFrameBits(tc));
        return EncodeFrames(frames, fps, sampleRate, options);
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
        options ??= new Options();

        double samplesPerBit = sampleRate / (fps * 80.0);
        int totalBits = frames.Count * 80;

        // 各ビットの境界（+ "1" のビット中央）に遷移位置（サンプル単位・実数）を並べる
        var transitions = new List<double>(totalBits * 2);
        int globalBit = 0;
        foreach (var frame in frames)
        {
            for (int b = 0; b < 80; b++, globalBit++)
            {
                double boundary = globalBit * samplesPerBit;
                transitions.Add(boundary);                  // 境界遷移（全ビット共通）
                if (frame[b])
                    transitions.Add(boundary + samplesPerBit * 0.5); // "1" の中央遷移
            }
        }

        int totalSamples = (int)Math.Round(totalBits * samplesPerBit);
        var samples = new float[totalSamples];

        float amp = options.Amplitude;
        float level = amp; // 開始レベル（position 0 の境界遷移で反転する）
        int idx = 0;

        var rng = options.NoiseAmplitude > 0 ? new Random(options.NoiseSeed) : null;

        for (int n = 0; n < totalSamples; n++)
        {
            while (idx < transitions.Count && transitions[idx] <= n)
            {
                level = -level;
                idx++;
            }

            float value = level;
            if (options.Invert) value = -value;
            value += options.DcOffset;
            if (rng != null)
                value += (float)((rng.NextDouble() * 2.0 - 1.0) * options.NoiseAmplitude);

            samples[n] = value;
        }

        return samples;
    }

    private static void SetBits(bool[] bits, int start, int count, int value)
    {
        for (int i = 0; i < count; i++)
            bits[start + i] = ((value >> i) & 1) != 0;
    }
}
