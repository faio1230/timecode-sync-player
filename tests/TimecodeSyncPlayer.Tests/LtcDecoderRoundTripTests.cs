using System;
using System.Collections.Generic;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// LtcDecoder のラウンドトリップ（エンコード→デコード→一致）および異常系テスト。
///
/// <para>
/// 波形は <see cref="LtcTestSignalGenerator"/> で生成する。生成器は LtcDecoder の
/// デコード実装を正として BMC 変調波形を作るため、「デコーダが受理する波形」の回帰網となる。
/// </para>
/// </summary>
public class LtcDecoderRoundTripTests
{
    /// <summary>波形を1回で書き込み、デコードされた全フレームを取り出す。</summary>
    private static List<LtcTimecode> DecodeAll(float[] samples, int sampleRate, double fps)
    {
        var decoder = new LtcDecoder(sampleRate, fps);
        decoder.Write(samples, samples.Length);
        return Drain(decoder);
    }

    private static List<LtcTimecode> Drain(LtcDecoder decoder)
    {
        var result = new List<LtcTimecode>();
        LtcTimecode? tc;
        while ((tc = decoder.Read()) != null)
            result.Add(tc);
        return result;
    }

    // ── カテゴリ1: 基本ラウンドトリップ（fps × サンプルレート × 代表TC） ──────────

    public static IEnumerable<object[]> BasicRoundTripCases()
    {
        int[] sampleRates = { 44100, 48000, 96000 };
        int[] fpsValues = { 24, 25, 30 };
        foreach (var sr in sampleRates)
        {
            foreach (var fps in fpsValues)
            {
                int last = fps - 1;
                yield return new object[] { sr, fps, new LtcTimecode(0, 0, 0, 0, false) };
                yield return new object[] { sr, fps, new LtcTimecode(23, 59, 59, last, false) };
                yield return new object[] { sr, fps, new LtcTimecode(10, 20, 30, last / 2, false) };
                yield return new object[] { sr, fps, new LtcTimecode(1, 2, 3, 4, false) };
            }
        }
    }

    [Theory]
    [MemberData(nameof(BasicRoundTripCases))]
    public void RoundTrip_DecodesExactTimecode(int sampleRate, int fps, LtcTimecode tc)
    {
        // 同期確立のため同一フレームを複数回連続させる
        var stream = new List<LtcTimecode> { tc, tc, tc, tc };
        var samples = LtcTestSignalGenerator.Generate(stream, fps, sampleRate);

        var decoded = DecodeAll(samples, sampleRate, fps);

        decoded.Should().NotBeEmpty("生成波形は少なくとも1フレームはデコードできるはず");
        decoded.Should().OnlyContain(d => d == tc);
    }

    [Theory]
    [MemberData(nameof(BasicRoundTripCases))]
    public void RoundTrip_ReportsCorrectEstimatedFps(int sampleRate, int fps, LtcTimecode tc)
    {
        var stream = new List<LtcTimecode> { tc, tc, tc, tc };
        var samples = LtcTestSignalGenerator.Generate(stream, fps, sampleRate);

        var decoder = new LtcDecoder(sampleRate, fps);
        decoder.Write(samples, samples.Length);

        decoder.EstimatedFps.Should().Be((double)fps);
    }

    /// <summary>
    /// コンストラクタに誤った fps ヒント（25）を渡しても、実際は 30fps の波形を
    /// 与えれば BMC クロックリカバリの指数移動平均（EMA）により EstimatedFps が
    /// 真値へ収束することを確認する。EMA の減衰率は 0.9/遷移で、1フレーム
    /// （80ビット、100件超の遷移）内で誤差はほぼ解消される。
    /// </summary>
    [Fact]
    public void RoundTrip_WrongFpsHint_EstimatedFpsConvergesToActual()
    {
        const int sampleRate = 48000;
        const int actualFps = 30;
        const double wrongHint = 25.0;
        var tc = new LtcTimecode(1, 2, 3, 4, false);
        var stream = new List<LtcTimecode> { tc, tc, tc, tc };
        var samples = LtcTestSignalGenerator.Generate(stream, actualFps, sampleRate);

        var decoder = new LtcDecoder(sampleRate, wrongHint);
        decoder.Write(samples, samples.Length);

        decoder.EstimatedFps.Should().Be((double)actualFps,
            "誤ったfpsヒントで初期化しても、実波形のビットクロックへEMAで収束するはず");
    }

    // ── カテゴリ2: 連続フレーム列（連番で進む） ──────────────────────────────

    [Theory]
    [InlineData(24, 48000)]
    [InlineData(25, 48000)]
    [InlineData(30, 48000)]
    [InlineData(25, 44100)]
    public void RoundTrip_ConsecutiveFrames_DecodeInSequence(int fps, int sampleRate)
    {
        const int frameCount = 40;
        var start = new LtcTimecode(1, 2, 3, 0, false);
        var expected = new List<LtcTimecode>();
        var cur = start;
        for (int i = 0; i < frameCount; i++)
        {
            expected.Add(cur);
            cur = LtcTestSignalGenerator.Increment(cur, fps);
        }

        var samples = LtcTestSignalGenerator.Generate(expected, fps, sampleRate);
        var decoded = DecodeAll(samples, sampleRate, fps);

        // 少なくとも大半のフレームがデコードでき、連続部分列として一致すること
        decoded.Count.Should().BeGreaterThanOrEqualTo(frameCount - 2);

        // デコード列が期待列の（先頭数フレームずれを許容した）連続部分列であることを確認
        int offset = expected.FindIndex(e => e == decoded[0]);
        offset.Should().BeGreaterThanOrEqualTo(0, "デコードされた先頭は期待列のいずれかに一致するはず");
        decoded.Count.Should().BeLessThanOrEqualTo(expected.Count - offset,
            "デコーダが期待列を超えるフレーム数を返した場合はここで検出する（範囲外例外ではなく明確な失敗として）");
        for (int i = 0; i < decoded.Count; i++)
            decoded[i].Should().Be(expected[offset + i], $"index {i} が連番で一致するはず");
    }

    // ── カテゴリ3: 途中からの受信（同期回復） ──────────────────────────────

    [Theory]
    [InlineData(25, 48000)]
    [InlineData(30, 48000)]
    public void RoundTrip_StartMidStream_RecoversSync(int fps, int sampleRate)
    {
        var start = new LtcTimecode(5, 10, 15, 0, false);
        var stream = new List<LtcTimecode>();
        var cur = start;
        for (int i = 0; i < 12; i++) { stream.Add(cur); cur = LtcTestSignalGenerator.Increment(cur, fps); }

        var full = LtcTestSignalGenerator.Generate(stream, fps, sampleRate);

        // フレーム境界でない位置（1.37ビット分ほど）から切り出して与える
        int cut = (int)(sampleRate / (fps * 80.0) * 1.37);
        var partial = new float[full.Length - cut];
        Array.Copy(full, cut, partial, 0, partial.Length);

        var decoded = DecodeAll(partial, sampleRate, fps);

        decoded.Should().NotBeEmpty("途中受信でも同期回復して読めるはず");
        // 読めたフレームはすべて期待列に含まれ、連続していること
        int offset = stream.FindIndex(e => e == decoded[0]);
        offset.Should().BeGreaterThanOrEqualTo(0);
        decoded.Count.Should().BeLessThanOrEqualTo(stream.Count - offset,
            "デコーダが期待列を超えるフレーム数を返した場合はここで検出する（範囲外例外ではなく明確な失敗として）");
        for (int i = 0; i < decoded.Count; i++)
            decoded[i].Should().Be(stream[offset + i]);
    }

    // ── カテゴリ4: チャンク分割（Write を細切れにしても一致） ───────────────

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(97)]
    [InlineData(1000)]
    public void RoundTrip_ChunkedWrite_MatchesSingleWrite(int chunkSize)
    {
        const int fps = 25;
        const int sampleRate = 48000;
        var stream = new List<LtcTimecode>
        {
            new(12, 34, 56, 7, false), new(12, 34, 56, 8, false),
            new(12, 34, 56, 9, false), new(12, 34, 56, 10, false),
        };
        var samples = LtcTestSignalGenerator.Generate(stream, fps, sampleRate);

        var single = DecodeAll(samples, sampleRate, fps);

        var chunked = new LtcDecoder(sampleRate, fps);
        for (int off = 0; off < samples.Length; off += chunkSize)
        {
            int count = Math.Min(chunkSize, samples.Length - off);
            var buf = new float[count];
            Array.Copy(samples, off, buf, 0, count);
            chunked.Write(buf, count);
        }
        var chunkedResult = Drain(chunked);

        chunkedResult.Should().Equal(single, "チャンク分割は結果に影響しないはず");
        single.Should().NotBeEmpty();
    }

    // ── カテゴリ5: 振幅変化（小振幅・大振幅クリップ気味） ──────────────────

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.1f)]
    [InlineData(1.0f)]
    [InlineData(3.0f)] // クリップ気味の大振幅
    public void RoundTrip_VaryingAmplitude_StillDecodes(float amplitude)
    {
        const int fps = 25;
        const int sampleRate = 48000;
        var tc = new LtcTimecode(8, 15, 30, 12, false);
        var stream = new List<LtcTimecode> { tc, tc, tc, tc };
        var opts = new LtcTestSignalGenerator.Options { Amplitude = amplitude };

        var samples = LtcTestSignalGenerator.Generate(stream, fps, sampleRate, opts);
        var decoded = DecodeAll(samples, sampleRate, fps);

        decoded.Should().NotBeEmpty();
        decoded.Should().OnlyContain(d => d == tc);
    }

    [Fact]
    public void RoundTrip_WithDcOffset_StillDecodes()
    {
        const int fps = 25;
        const int sampleRate = 48000;
        var tc = new LtcTimecode(9, 8, 7, 6, false);
        var stream = new List<LtcTimecode> { tc, tc, tc, tc };
        // オフセットが振幅未満ならゼロクロッシングは維持される（振幅と同値/超だと消失する）
        var opts = new LtcTestSignalGenerator.Options { Amplitude = 1.0f, DcOffset = 0.3f };

        var samples = LtcTestSignalGenerator.Generate(stream, fps, sampleRate, opts);
        var decoded = DecodeAll(samples, sampleRate, fps);

        decoded.Should().NotBeEmpty("振幅未満のDCオフセットではゼロクロッシングが残るはず");
        decoded.Should().OnlyContain(d => d == tc);
    }

    // ── カテゴリ6: 極性反転（BMCは極性無依存） ────────────────────────────

    [Theory]
    [InlineData(24, 48000)]
    [InlineData(25, 48000)]
    [InlineData(30, 44100)]
    public void RoundTrip_InvertedPolarity_StillDecodes(int fps, int sampleRate)
    {
        var tc = new LtcTimecode(3, 21, 9, 5, false);
        var stream = new List<LtcTimecode> { tc, tc, tc, tc };
        var opts = new LtcTestSignalGenerator.Options { Invert = true };

        var samples = LtcTestSignalGenerator.Generate(stream, fps, sampleRate, opts);
        var decoded = DecodeAll(samples, sampleRate, fps);

        decoded.Should().NotBeEmpty("BMCは極性無依存なので反転波形でもデコードできるはず");
        decoded.Should().OnlyContain(d => d == tc);
    }

    // ── カテゴリ7: ノイズ耐性（固定シードの軽微なノイズ） ─────────────────

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.15)]
    public void RoundTrip_WithNoise_StillDecodes(double noise)
    {
        const int fps = 25;
        const int sampleRate = 48000;
        var tc = new LtcTimecode(22, 11, 44, 3, false);
        var stream = new List<LtcTimecode> { tc, tc, tc, tc, tc };
        var opts = new LtcTestSignalGenerator.Options
        {
            Amplitude = 1.0f,
            NoiseAmplitude = noise,
            NoiseSeed = 4242,
        };

        var samples = LtcTestSignalGenerator.Generate(stream, fps, sampleRate, opts);
        var decoded = DecodeAll(samples, sampleRate, fps);

        decoded.Should().NotBeEmpty("軽微なノイズ下でもデコードできるはず");
        decoded.Should().Contain(tc);
    }

    // ── カテゴリ8: 無効入力（null を返し続け、例外を投げない） ──────────────

    [Fact]
    public void InvalidInput_Silence_ReturnsNullNoThrow()
    {
        var decoder = new LtcDecoder(48000, 25);
        var silence = new float[48000];

        Action act = () => decoder.Write(silence, silence.Length);

        act.Should().NotThrow();
        Drain(decoder).Should().BeEmpty();
    }

    [Fact]
    public void InvalidInput_WhiteNoise_ReturnsNullNoThrow()
    {
        var decoder = new LtcDecoder(48000, 25);
        var rng = new Random(9001);
        var noise = new float[48000 * 2];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

        Action act = () => decoder.Write(noise, noise.Length);

        act.Should().NotThrow();
        Drain(decoder).Should().BeEmpty("純粋なホワイトノイズは有効フレームを生成しないはず");
    }

    [Theory]
    [InlineData(1000.0)]
    [InlineData(50.0)]
    [InlineData(19200.0)] // ≒ 25fps LTC のビットレート近傍
    public void InvalidInput_SineWave_ReturnsNullNoThrow(double frequency)
    {
        const int sampleRate = 48000;
        var decoder = new LtcDecoder(sampleRate, 25);
        var sine = new float[sampleRate];
        for (int i = 0; i < sine.Length; i++)
            sine[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);

        Action act = () => decoder.Write(sine, sine.Length);

        act.Should().NotThrow();
        Drain(decoder).Should().BeEmpty("正弦波は同期ワードを生成しないはず");
    }

    // ── 追加: ドロップフレーム（29.97DF）ラウンドトリップ ─────────────────
    // デコーダは bit10 を DropFrame フラグとして読み、LtcTimecode.DropFrame に反映する。
    // DF のフレーム計数意味は検証しない（デコーダも検証しない）が、フラグの往復を確認する。

    [Fact]
    public void RoundTrip_DropFrameFlag_IsPreserved()
    {
        const int fps = 30;
        const int sampleRate = 48000;
        var tc = new LtcTimecode(1, 0, 0, 2, DropFrame: true);
        var stream = new List<LtcTimecode> { tc, tc, tc, tc };

        var samples = LtcTestSignalGenerator.Generate(stream, fps, sampleRate);
        var decoded = DecodeAll(samples, sampleRate, fps);

        decoded.Should().NotBeEmpty();
        decoded.Should().OnlyContain(d => d == tc);
        decoded.Should().OnlyContain(d => d.DropFrame);
    }
}
