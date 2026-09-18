using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 0.4.5-C3: 判定は最大ギャップで行う。受け入れ条件は検証機の実素材の測定値
/// （ffprobe、ファイル全体、先頭・末尾ギャップ込み）をそのまま使う。
/// </summary>
public sealed class GopScanVerdictTests
{
    // 検証機の実測（M8 はプロダクション素材、M1〜M7 は YouTube から落としたサンプル）。
    // 出る / 出ないがこの表のとおりになることが受け入れ条件。
    [Theory]
    [InlineData("M8 production h264 4K59.94", 2438, 0.501, GopSeekQuality.Ok)]
    [InlineData("M1 prores 全フレーム I", 3510, 0.017, GopSeekQuality.Ok)]
    [InlineData("M2 h264", 27, 5.300, GopSeekQuality.Warning)]
    [InlineData("M3 vp9 4K60", 85, 6.633, GopSeekQuality.Warning)]
    [InlineData("M4 vp9 1080p60", 43, 6.900, GopSeekQuality.Warning)]
    [InlineData("M5 av1 4K24", 126, 7.000, GopSeekQuality.Warning)]
    [InlineData("M6 av1 4K24", 149, 6.708, GopSeekQuality.Warning)]
    [InlineData("M7 av1 4K24", 251, 7.000, GopSeekQuality.Warning)]
    public void 実素材の測定値で判定が一致する(string label, int keyframes, double maxGap, GopSeekQuality expected)
    {
        GopScanVerdict.Judge(keyframes, maxGap).Should().Be(expected, label);
    }

    [Fact]
    public void 中央値では見逃す素材を最大ギャップで捕まえる()
    {
        // M6: 中央値 0.708 秒は推奨（1〜2 秒）に収まるが、最大は 6.708 秒。
        // その区間へシークすると実際に遅い（実測 2,164ms）。
        GopScanVerdict.Judge(keyframes: 149, maxGapSeconds: 6.708)
            .Should().Be(GopSeekQuality.Warning);
    }

    [Fact]
    public void 十秒を超えるとエラー扱いになる()
    {
        GopScanVerdict.Judge(keyframes: 9, maxGapSeconds: 10.001)
            .Should().Be(GopSeekQuality.Error);
        GopScanVerdict.Judge(keyframes: 9, maxGapSeconds: 10.000)
            .Should().Be(GopSeekQuality.Warning, "境界はエラーに含めない");
    }

    [Fact]
    public void スキャンできなければ判定しない()
    {
        // 誤検出より無検出が安全。キーフレームが取れない素材で警告を出さない。
        GopScanVerdict.Judge(keyframes: 0, maxGapSeconds: 30.0)
            .Should().Be(GopSeekQuality.Unknown);
        GopScanVerdict.Judge(keyframes: 10, maxGapSeconds: double.NaN)
            .Should().Be(GopSeekQuality.Unknown);
    }

    [Fact]
    public void 表示はOKとUnknownで空になる()
    {
        GopScanVerdict.Format(GopSeekQuality.Ok, 0.5).Should().BeEmpty();
        GopScanVerdict.Format(GopSeekQuality.Unknown, 0.0).Should().BeEmpty();
        GopScanVerdict.Format(GopSeekQuality.Warning, 6.7).Should().Contain("6.7");
        GopScanVerdict.Format(GopSeekQuality.Error, 12.3).Should().Contain("12.3");
    }
}
