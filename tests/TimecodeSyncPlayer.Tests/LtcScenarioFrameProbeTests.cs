using System.Drawing;
using System.Drawing.Drawing2D;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// LTC シナリオ E2E の画素判定ヘルパーを合成画像で固定する。
/// 黒は「平均輝度 &lt; 8/255 かつ黒画素 >= 99%」、参照一致は
/// 「平均色距離 &lt; 60 かつ最近傍かつ画素差分 &lt; 12/255」。
/// </summary>
public sealed class LtcScenarioFrameProbeTests
{
    [Fact]
    public void MeasureCenter_MeasuresOnlyTheCenter60PercentRegion()
    {
        using Bitmap bitmap = Solid(Color.Green);
        Fill(bitmap, Color.Red, 20, 20, 60, 60);

        FrameSignature signature = LtcScenarioFrameProbe.MeasureCenter(bitmap);

        signature.MeanR.Should().BeApproximately(255, 0.001);
        signature.MeanG.Should().BeApproximately(0, 0.001);
        signature.MeanB.Should().BeApproximately(0, 0.001);
        signature.BlackFraction.Should().Be(0);
        signature.IsBlack.Should().BeFalse();
        signature.SampleCount.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void IsBlack_AcceptsFramesBelowTheLuminanceThreshold(int level)
    {
        using Bitmap bitmap = Solid(Color.FromArgb(level, level, level));

        FrameSignature signature = LtcScenarioFrameProbe.MeasureCenter(bitmap);

        signature.IsBlack.Should().BeTrue();
    }

    [Fact]
    public void IsBlack_RejectsGreyAboveTheLuminanceThreshold()
    {
        using Bitmap bitmap = Solid(Color.FromArgb(20, 20, 20));

        FrameSignature signature = LtcScenarioFrameProbe.MeasureCenter(bitmap);

        signature.IsBlack.Should().BeFalse();
    }

    [Fact]
    public void IsBlack_RejectsMixedFramesEvenWhenTheMeanIsLow()
    {
        using Bitmap bitmap = Solid(Color.Black);
        Fill(bitmap, Color.White, 20, 20, 60, 60);

        FrameSignature signature = LtcScenarioFrameProbe.MeasureCenter(bitmap);

        signature.MeanLuminance.Should().BeGreaterThan(8);
        signature.IsBlack.Should().BeFalse();
    }

    [Fact]
    public void Match_AcceptsCloseFrameOfTheSameTrack()
    {
        using Bitmap reference = Solid(Color.FromArgb(255, 0, 0));
        using Bitmap current = Solid(Color.FromArgb(253, 1, 0));
        var set = new ReferenceSet();
        set.Add("A", "head", "ref_A_head", LtcScenarioFrameProbe.MeasureCenter(reference));

        ReferenceMatch match = set.Match(LtcScenarioFrameProbe.MeasureCenter(current));

        match.IsMatch.Should().BeTrue();
        match.MatchesTrack("A").Should().BeTrue();
        match.ColorDistance.Should().BeLessThan(60);
        match.PixelDifference.Should().BeLessThan(12);
    }

    [Fact]
    public void Match_RejectsAnotherColor()
    {
        using Bitmap reference = Solid(Color.FromArgb(255, 0, 0));
        using Bitmap current = Solid(Color.FromArgb(0, 255, 0));
        var set = new ReferenceSet();
        set.Add("A", "head", "ref_A_head", LtcScenarioFrameProbe.MeasureCenter(reference));

        ReferenceMatch match = set.Match(LtcScenarioFrameProbe.MeasureCenter(current));

        match.IsMatch.Should().BeFalse();
        match.ColorDistance.Should().BeGreaterThan(60);
    }

    [Fact]
    public void Match_RejectsSameColorWithDifferentPixels()
    {
        using Bitmap reference = SplitHorizontal(Color.Red, Color.Blue);
        using Bitmap current = SplitHorizontal(Color.Blue, Color.Red);
        var set = new ReferenceSet();
        set.Add("A", "head", "ref_A_head", LtcScenarioFrameProbe.MeasureCenter(reference));

        ReferenceMatch match = set.Match(LtcScenarioFrameProbe.MeasureCenter(current));

        match.ColorDistance.Should().BeApproximately(0, 0.001);
        match.PixelDifference.Should().BeGreaterThan(12);
        match.IsMatch.Should().BeFalse();
    }

    [Fact]
    public void Match_RejectsFrameThatIsEquallyCloseToTwoReferences()
    {
        using Bitmap black = Solid(Color.Black);
        using Bitmap bright = Solid(Color.FromArgb(100, 0, 0));
        using Bitmap between = Solid(Color.FromArgb(50, 0, 0));
        var set = new ReferenceSet();
        set.Add("A", "tail", "ref_A_tail", LtcScenarioFrameProbe.MeasureCenter(black));
        set.Add("B", "head", "ref_B_head", LtcScenarioFrameProbe.MeasureCenter(bright));

        ReferenceMatch match = set.Match(LtcScenarioFrameProbe.MeasureCenter(between));

        match.ColorDistance.Should().BeApproximately(50, 0.001);
        match.IsMatch.Should().BeFalse();
    }

    [Fact]
    public void Match_SelectsTheTrackWhoseReferenceIsClosest()
    {
        using Bitmap signatureA = Solid(Color.FromArgb(255, 0, 0));
        using Bitmap signatureB = Solid(Color.FromArgb(0, 255, 0));
        using Bitmap signatureC = Solid(Color.FromArgb(0, 0, 255));
        using Bitmap current = Solid(Color.FromArgb(252, 2, 0));
        var set = new ReferenceSet();
        set.Add("A", "head", "ref_A_head", LtcScenarioFrameProbe.MeasureCenter(signatureA));
        set.Add("B", "head", "ref_B_head", LtcScenarioFrameProbe.MeasureCenter(signatureB));
        set.Add("C", "head", "ref_C_head", LtcScenarioFrameProbe.MeasureCenter(signatureC));

        ReferenceMatch match = set.Match(LtcScenarioFrameProbe.MeasureCenter(current));

        match.IsMatch.Should().BeTrue();
        match.Reference!.TrackSymbol.Should().Be("A");
        match.MatchesTrack("A").Should().BeTrue();
        match.MatchesTrack("B").Should().BeFalse();
        match.MatchesTrack("C").Should().BeFalse();
    }

    /// <summary>
    /// 期待の参照が他の参照と同定閾値（60）未満の距離にある組は「同定不能」とし、判定を失敗にしない
    /// （検証機でほぼ黒の head 参照が別素材の head と区別できなかった事例）。
    /// </summary>
    [Fact]
    public void IsAmbiguous_FlagsReferencePairsWithinTheIdentificationThreshold()
    {
        using Bitmap nearA = Solid(Color.FromArgb(100, 100, 100));
        using Bitmap nearB = Solid(Color.FromArgb(130, 100, 100)); // A との距離 30
        using Bitmap far = Solid(Color.FromArgb(250, 250, 250));
        using Bitmap dark = Solid(Color.Black);
        var set = new ReferenceSet();
        set.Add("A", "head", "ref_A_head", LtcScenarioFrameProbe.MeasureCenter(nearA));
        set.Add("B", "head", "ref_B_head", LtcScenarioFrameProbe.MeasureCenter(nearB));
        set.Add("C", "head", "ref_C_head", LtcScenarioFrameProbe.MeasureCenter(far));
        set.Add("C", "tail", "ref_C_tail", LtcScenarioFrameProbe.MeasureCenter(dark));

        set.IsAmbiguous("A", "head").Should().BeTrue("B の head と距離 30");
        set.IsAmbiguous("B", "head").Should().BeTrue();
        set.IsAmbiguous("C", "head").Should().BeFalse("C の tail は黒で距離が大きい");
        set.IsAmbiguous("C", null).Should().BeFalse();
        set.IsAmbiguous("A", "tail").Should().BeFalse("参照が無い");
    }

    [Fact]
    public void DescribeNearestKnownColor_IsInformationalOnly()
    {
        using Bitmap bitmap = Solid(Color.FromArgb(250, 250, 0));

        string description = LtcScenarioFrameProbe.DescribeNearestKnownColor(
            LtcScenarioFrameProbe.MeasureCenter(bitmap));

        description.Should().StartWith("yellow");
    }

    /// <summary>
    /// 参照が黒の素材（冒頭が全面黒など）では「黒」と「参照で静止」を画像で区別できないため、
    /// 黒判定を課さない。判定側はこのフラグで切り替える。
    /// </summary>
    [Fact]
    public void ReferenceSet_DetectsBlackReferencesForBlackJudgment()
    {
        using Bitmap black = Solid(Color.Black);
        using Bitmap red = Solid(Color.Red);
        var set = new ReferenceSet();
        set.Add("A", "head", "ref_A_head", LtcScenarioFrameProbe.MeasureCenter(black));
        set.Add("A", "tail", "ref_A_tail", LtcScenarioFrameProbe.MeasureCenter(red));
        set.Add("B", "head", "ref_B_head", LtcScenarioFrameProbe.MeasureCenter(red));
        set.Add("B", "tail", "ref_B_tail", LtcScenarioFrameProbe.MeasureCenter(red));

        set.IsTrackBlack("A").Should().BeTrue("head が黒のトラックは黒判定を課さない");
        set.IsHeadReferenceBlack("A").Should().BeTrue();
        set.IsTrackBlack("B").Should().BeFalse();
        set.IsHeadReferenceBlack("B").Should().BeFalse();
        set.HasReferences("A").Should().BeTrue();
        set.HasReferences("C").Should().BeFalse();
        set.IsTrackBlack("C").Should().BeFalse();
    }

    /// <summary>
    /// 参照採取の取り直し判定: 同じ表示フレームを再度読むと同一の署名になる。
    /// D25 のシーク前フレーム（PTS だけ目標）を参照として固定しないための検出に使う。
    /// </summary>
    [Fact]
    public void IsSameFrameAs_AcceptsIdenticalCaptures()
    {
        using Bitmap bitmap = Solid(Color.FromArgb(255, 240, 0));

        FrameSignature first = LtcScenarioFrameProbe.MeasureCenter(bitmap);
        FrameSignature second = LtcScenarioFrameProbe.MeasureCenter(bitmap);

        first.IsSameFrameAs(second).Should().BeTrue();
    }

    [Fact]
    public void IsSameFrameAs_RejectsDifferentColors()
    {
        using Bitmap white = Solid(Color.White);
        using Bitmap yellow = Solid(Color.FromArgb(255, 240, 0));

        LtcScenarioFrameProbe.MeasureCenter(white)
            .IsSameFrameAs(LtcScenarioFrameProbe.MeasureCenter(yellow)).Should().BeFalse();
    }

    [Fact]
    public void IsSameFrameAs_RejectsSameMeanWithDifferentPixels()
    {
        using Bitmap leftRed = SplitHorizontal(Color.Red, Color.Blue);
        using Bitmap leftBlue = SplitHorizontal(Color.Blue, Color.Red);

        LtcScenarioFrameProbe.MeasureCenter(leftRed)
            .IsSameFrameAs(LtcScenarioFrameProbe.MeasureCenter(leftBlue)).Should().BeFalse();
    }

    [Fact]
    public void IsSameFrameAs_RejectsMissingSamples()
    {
        FrameSignature empty = default;

        empty.IsSameFrameAs(empty).Should().BeFalse("サンプル無しの署名を同一扱いしない");
    }

    private static Bitmap Solid(Color color)
    {
        var bitmap = new Bitmap(100, 100);
        Fill(bitmap, color, 0, 0, 100, 100);
        return bitmap;
    }

    private static Bitmap SplitHorizontal(Color left, Color right)
    {
        var bitmap = new Bitmap(100, 100);
        Fill(bitmap, left, 0, 0, 50, 100);
        Fill(bitmap, right, 50, 0, 50, 100);
        return bitmap;
    }

    private static void Fill(Bitmap bitmap, Color color, int x, int y, int width, int height)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, x, y, width, height);
    }
}
