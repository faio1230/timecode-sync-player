using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.5 B3「LTC のフレームの検査」（設計書 docs/design/v0.6.5-small-fixes.md 4 節）。
/// BCD の各桁が 9 以下、同期ワードから次の同期ワードまでがちょうど 80 ビット、の 2 つを検査し、
/// 通らない LTC のフレームはキューに入れない。受け始め（前の同期ワードが無い）の最初の 1 つは長さを
/// 確かめられないので使わない。極性補正ビットは数えるだけで検査には使わない。
/// </summary>
public class LtcFrameCheckTests
{
    private const int SampleRate = 48000;

    // ── 化けた列が落ちる ──────────────────────────────────────────

    [Fact]
    public void ShiftedFrame_AtReceiveStart_IsDropped_AndNextFramesPass()
    {
        // 00:00:07:20 の 80 ビットを 1 ビットずらす（新しい bit i = 元の bit i+1）と 00:00:03:10 に見える。
        // 値の範囲の検査は通ってしまう値なので、受け始めの 1 つ目を使わないことで落とす。
        bool[] shifted = ShiftDataOneBit(Bits(new LtcTimecode(0, 0, 7, 20, false)));
        DecodeData(shifted).Should().Be(new LtcTimecode(0, 0, 3, 10, false), "前提: 1 ビットずれで 03:10 に化ける");

        var stream = new List<bool>(shifted);
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 21, false)));
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 22, false)));

        (List<LtcTimecode> decoded, LtcFrameCheckStats stats) = Decode(stream, 25);

        decoded.Should().Equal(new LtcTimecode(0, 0, 7, 21, false), new LtcTimecode(0, 0, 7, 22, false));
        stats.StartSkipped.Should().Be(1);
        stats.Accepted.Should().Be(2);
    }

    [Fact]
    public void ShiftedFrame_With81Bits_MidStream_IsDroppedByLength()
    {
        // 正しい 07:19 の後に、1 ビット余計に入って 64 ビットのデータ部が 03:10 に化けた 81 ビットの LTC のフレーム。
        bool[] shifted = ShiftDataOneBit(Bits(new LtcTimecode(0, 0, 7, 20, false)));
        var stream = new List<bool>();
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 18, false)));
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 19, false)));
        stream.Add(false);                 // 余計な 1 ビット
        stream.AddRange(shifted);          // 64 + 16 ビット → 同期ワードまで 81 ビット
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 21, false)));
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 22, false)));

        (List<LtcTimecode> decoded, LtcFrameCheckStats stats) = Decode(stream, 25);

        decoded.Should().NotContain(new LtcTimecode(0, 0, 3, 10, false));
        // 07:21 は 03:10 の同期ワードから数えて 80 ビットなので通る。落ちるのは 81 ビットの 03:10 の 1 つだけ。
        decoded.Should().Equal(
            new LtcTimecode(0, 0, 7, 19, false),
            new LtcTimecode(0, 0, 7, 21, false),
            new LtcTimecode(0, 0, 7, 22, false));
        stats.RejectedLength.Should().Be(1);
    }

    [Fact]
    public void Frame_With79Bits_MidStream_IsDroppedByLength()
    {
        bool[] short79 = Bits(new LtcTimecode(0, 0, 7, 20, false)).Skip(1).ToArray(); // bit 0 が抜けた
        var stream = new List<bool>();
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 18, false)));
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 19, false)));
        stream.AddRange(short79);
        stream.AddRange(Bits(new LtcTimecode(0, 0, 7, 21, false)));

        (List<LtcTimecode> decoded, LtcFrameCheckStats stats) = Decode(stream, 25);

        decoded.Should().Equal(new LtcTimecode(0, 0, 7, 19, false), new LtcTimecode(0, 0, 7, 21, false));
        stats.RejectedLength.Should().Be(1);
    }

    [Theory]
    [InlineData(0, 4)]    // フレームの 1 の位（bits 0-3）
    [InlineData(16, 4)]   // 秒の 1 の位（bits 16-19）
    [InlineData(32, 4)]   // 分の 1 の位（bits 32-35）
    [InlineData(48, 4)]   // 時の 1 の位（bits 48-51）
    public void Frame_WithBcdDigitAbove9_IsDropped(int start, int width)
    {
        // 1 の位を 10〜15 にすると、10 の位が 0 なら値の範囲の検査（フレーム 29 以下など）は通ってしまう。
        foreach (int digit in new[] { 10, 15 })
        {
            bool[] bad = Bits(new LtcTimecode(0, 0, 0, 0, false));
            for (int i = 0; i < width; i++)
                bad[start + i] = ((digit >> i) & 1) != 0;

            var stream = new List<bool>();
            stream.AddRange(Bits(new LtcTimecode(0, 0, 0, 0, false)));
            stream.AddRange(Bits(new LtcTimecode(0, 0, 0, 1, false)));
            stream.AddRange(bad);
            stream.AddRange(Bits(new LtcTimecode(0, 0, 0, 3, false)));

            (List<LtcTimecode> decoded, LtcFrameCheckStats stats) = Decode(stream, 25);

            decoded.Should().Equal(new LtcTimecode(0, 0, 0, 1, false), new LtcTimecode(0, 0, 0, 3, false));
            stats.RejectedBcd.Should().Be(1, $"bits {start}-{start + width - 1} = {digit}");
        }
    }

    // ── 正しい列が全部通る ─────────────────────────────────────────

    public static IEnumerable<object[]> ValidSequences()
    {
        // fps, 始まりのタイムコード（時分秒の境界をまたぐ位置）
        foreach (int fps in new[] { 24, 25, 30 })
        {
            yield return new object[] { (double)fps, new LtcTimecode(0, 0, 0, 0, false) };
            yield return new object[] { (double)fps, new LtcTimecode(0, 0, 59, fps - 3, false) };   // 分の境界
            yield return new object[] { (double)fps, new LtcTimecode(0, 59, 59, fps - 3, false) };  // 時の境界
            yield return new object[] { (double)fps, new LtcTimecode(9, 59, 59, fps - 3, false) };  // 時の 1 の位 9 → 10
            yield return new object[] { (double)fps, new LtcTimecode(23, 59, 59, fps - 3, false) }; // 23:59:59 の次
        }
        // 29.97 DF: 10 分の倍数でない分の境界（00/01 を飛ばす）、10 分の倍数の境界、23:59:59 の次
        double df = 30000.0 / 1001.0;
        yield return new object[] { df, new LtcTimecode(0, 0, 59, 27, true) };
        yield return new object[] { df, new LtcTimecode(0, 9, 59, 27, true) };
        yield return new object[] { df, new LtcTimecode(0, 59, 59, 27, true) };
        yield return new object[] { df, new LtcTimecode(23, 59, 59, 27, true) };
        // 29.97 NDF（DF フラグなし）
        yield return new object[] { df, new LtcTimecode(0, 0, 59, 27, false) };
    }

    [Theory]
    [MemberData(nameof(ValidSequences))]
    public void ValidSequence_AllFramesPass_ExceptReceiveStart(double fps, LtcTimecode first)
    {
        const int count = 8;
        var expected = new List<LtcTimecode> { first };
        for (int i = 1; i < count; i++)
            expected.Add(Next(expected[^1], fps));

        // 極性補正ビットの位置は 25 fps で bit 59、それ以外で bit 27（EBU Tech 3097-E 3.3 節、SMPTE 12M の通説）。
        int parityBit = Math.Round(fps) == 25 ? 59 : 27;
        var stream = new List<bool>();
        foreach (LtcTimecode tc in expected)
            stream.AddRange(LtcTestSignalGenerator.SetPolarityCorrection(Bits(tc), parityBit));

        (List<LtcTimecode> decoded, LtcFrameCheckStats stats) = Decode(stream, fps);

        decoded.Should().Equal(expected.Skip(1), "受け始めの 1 つ目は長さを確かめられないので使わない（v0.6.5 B3）");
        stats.StartSkipped.Should().Be(1);
        stats.RejectedLength.Should().Be(0);
        stats.RejectedBcd.Should().Be(0);
        stats.ParityMismatch.Should().Be(0);
        stats.Accepted.Should().Be(count - 1);
    }

    // ── 受け始め・再接続 ──────────────────────────────────────────

    [Fact]
    public void ReceiveStart_FirstFrameIsDropped_SecondPasses()
    {
        var stream = new List<bool>();
        stream.AddRange(Bits(new LtcTimecode(1, 2, 3, 4, false)));
        stream.AddRange(Bits(new LtcTimecode(1, 2, 3, 5, false)));

        (List<LtcTimecode> decoded, LtcFrameCheckStats stats) = Decode(stream, 25);

        decoded.Should().Equal(new LtcTimecode(1, 2, 3, 5, false));
        stats.StartSkipped.Should().Be(1);
    }

    [Fact]
    public void AfterSilence_FirstFrameIsDropped_SecondPasses()
    {
        // 無音（遷移が 2 ビットぶんより長く無い）は受け直しとみなし、最初の 1 つは長さを確かめられない扱い。
        var stats = new LtcFrameCheckStats();
        var decoder = new LtcDecoder(SampleRate, 25, stats);
        Feed(decoder, LtcTestSignalGenerator.EncodeBitStream(Concat(
            Bits(new LtcTimecode(0, 0, 1, 0, false)),
            Bits(new LtcTimecode(0, 0, 1, 1, false)),
            Bits(new LtcTimecode(0, 0, 1, 2, false))), 25, SampleRate));
        Feed(decoder, new float[SampleRate / 2]); // 0.5 秒の無音
        var after = Enumerable.Range(0, 6).Select(f => new LtcTimecode(0, 0, 2, f, false)).ToList();
        Feed(decoder, LtcTestSignalGenerator.EncodeBitStream(Concat(after.Select(Bits).ToArray()), 25, SampleRate));

        // 無音の後はハーフビットの推定が長い間隔に引っ張られ（既存の作り）、合い直すまで同期ワードが見えないことがある。
        // 見えた最初の同期ワードは受け始めの扱いで使わず、その次から受け取る。長さの不一致としては数えない。
        List<LtcTimecode> decoded = Drain(decoder);
        decoded.Take(2).Should().Equal(new LtcTimecode(0, 0, 1, 1, false), new LtcTimecode(0, 0, 1, 2, false));
        List<LtcTimecode> resumed = decoded.Skip(2).ToList();
        resumed.Should().NotBeEmpty();
        resumed.Should().NotContain(after[0]);
        int firstIndex = after.IndexOf(resumed[0]);
        firstIndex.Should().BeGreaterThan(0);
        resumed.Should().Equal(after.Skip(firstIndex), "受け直しの後は 1 つ目だけ使わず、続きは全部通る");
        stats.StartSkipped.Should().Be(2);
        stats.RejectedLength.Should().Be(0);
    }

    // ── 極性補正ビット（数えるだけ） ───────────────────────────────

    [Fact]
    public void Polarity_IsCountedOnly_FramesWithOddZerosStillPass()
    {
        // 0 の数が奇数（極性補正ビットが合っていない）でも受け取り、数えるだけ。
        var tcs = Enumerable.Range(0, 6).Select(f => new LtcTimecode(0, 0, 0, f, false)).ToList();
        var stream = new List<bool>();
        foreach (LtcTimecode tc in tcs)
        {
            bool[] bits = LtcTestSignalGenerator.SetPolarityCorrection(Bits(tc), 59);
            bits[59] = !bits[59]; // わざと外す
            stream.AddRange(bits);
        }

        (List<LtcTimecode> decoded, LtcFrameCheckStats stats) = Decode(stream, 25);

        decoded.Should().Equal(tcs.Skip(1));
        stats.ParityMismatch.Should().Be(5);
    }

    [Fact]
    public void Polarity_CountsWhichBitCarriesTheCorrection()
    {
        // 80 ビット全体の 0 の数の偶奇は、補正にどちらのビットを使っても同じになる。
        // どちらのビットが補正に使われているかは、ユーザーのビットが 0 の信号でそのビットが 1 になった回数で見る。
        var tcs = Enumerable.Range(0, 9).Select(f => new LtcTimecode(0, 0, 0, f, false)).ToList();

        var with27 = new List<bool>();
        var with59 = new List<bool>();
        foreach (LtcTimecode tc in tcs)
        {
            with27.AddRange(LtcTestSignalGenerator.SetPolarityCorrection(Bits(tc), 27));
            with59.AddRange(LtcTestSignalGenerator.SetPolarityCorrection(Bits(tc), 59));
        }

        (_, LtcFrameCheckStats s27) = Decode(with27, 30);
        (_, LtcFrameCheckStats s59) = Decode(with59, 25);

        s27.ParityMismatch.Should().Be(0);
        s59.ParityMismatch.Should().Be(0);
        s27.Bit27Ones.Should().BeGreaterThan(0);
        s27.Bit59Ones.Should().Be(0);
        s59.Bit59Ones.Should().BeGreaterThan(0);
        s59.Bit27Ones.Should().Be(0);
    }

    [Fact]
    public void Summary_FormatsAllCounters()
    {
        var stats = new LtcFrameCheckStats();
        var decoder = new LtcDecoder(SampleRate, 25, stats);
        Feed(decoder, LtcTestSignalGenerator.EncodeBitStream(Concat(
            Bits(new LtcTimecode(0, 0, 0, 0, false)),
            Bits(new LtcTimecode(0, 0, 0, 1, false))), 25, SampleRate));

        // 受け取った 00:00:00:01 は補正ビットなしで 0 の数が 66（偶数）なので parityMismatch=0。
        stats.FormatSummaryFields().Should().Be(
            "rejectedBcd=0 rejectedLength=0 startSkipped=1 accepted=1 parityMismatch=0 bit27Ones=0 bit59Ones=0");
    }

    // ── 補助 ──────────────────────────────────────────────────

    private static bool[] Bits(LtcTimecode tc) => LtcTestSignalGenerator.BuildFrameBits(tc);

    private static List<bool> Concat(params bool[][] frames) => frames.SelectMany(f => f).ToList();

    /// <summary>データ部（bits 0-63）を 1 ビットずらす（新しい bit i = 元の bit i+1、bit 63 は 0）。同期ワードはそのまま。</summary>
    private static bool[] ShiftDataOneBit(bool[] bits)
    {
        var shifted = (bool[])bits.Clone();
        for (int i = 0; i < 63; i++)
            shifted[i] = bits[i + 1];
        shifted[63] = false;
        return shifted;
    }

    private static LtcTimecode DecodeData(bool[] bits)
    {
        int Read(int start, int width)
        {
            int v = 0;
            for (int i = 0; i < width; i++)
                if (bits[start + i]) v |= 1 << i;
            return v;
        }
        return new LtcTimecode(
            Read(56, 2) * 10 + Read(48, 4),
            Read(40, 3) * 10 + Read(32, 4),
            Read(24, 3) * 10 + Read(16, 4),
            Read(8, 2) * 10 + Read(0, 4),
            bits[10]);
    }

    private static LtcTimecode Next(LtcTimecode tc, double fps)
    {
        int nominal = (int)Math.Round(fps);
        LtcTimecode n = LtcTestSignalGenerator.Increment(tc, nominal);
        // 29.97 DF: 10 の倍数でない分の頭の 00・01 は飛ばす
        if (tc.DropFrame && n.Seconds == 0 && n.Frames == 0 && n.Minutes % 10 != 0)
            n = n with { Frames = 2 };
        return n;
    }

    private static (List<LtcTimecode> Decoded, LtcFrameCheckStats Stats) Decode(IReadOnlyList<bool> stream, double fps)
    {
        var stats = new LtcFrameCheckStats();
        var decoder = new LtcDecoder(SampleRate, fps, stats);
        Feed(decoder, LtcTestSignalGenerator.EncodeBitStream(stream, fps, SampleRate));
        return (Drain(decoder), stats);
    }

    private static void Feed(LtcDecoder decoder, float[] samples) => decoder.Write(samples, samples.Length);

    private static List<LtcTimecode> Drain(LtcDecoder decoder)
    {
        var list = new List<LtcTimecode>();
        while (decoder.Read() is { } f)
            list.Add(f.Timecode);
        return list;
    }
}
