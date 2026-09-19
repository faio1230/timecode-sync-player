using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>0.4.7: 推奨外コーデックの警告。入力は shim のプロファイル表のデコーダ名。</summary>
public sealed class CodecAdviceTests
{
    [Theory]
    [InlineData("d3d11h264dec", CodecStanding.Recommended)]
    [InlineData("avdec_h264", CodecStanding.Recommended)]
    [InlineData("avdec_prores", CodecStanding.Acceptable)]
    [InlineData("d3d11vp9dec", CodecStanding.NotRecommended)]
    [InlineData("avdec_vp9", CodecStanding.NotRecommended)]
    [InlineData("d3d11av1dec", CodecStanding.NotRecommended)]
    [InlineData("dav1ddec", CodecStanding.NotRecommended)]
    [InlineData("d3d11h265dec", CodecStanding.NotRecommended)]
    [InlineData("avdec_h265", CodecStanding.NotRecommended)]
    [InlineData("decodebin(sysmem)", CodecStanding.NotRecommended)]
    [InlineData("raw", CodecStanding.Unknown)]
    [InlineData("", CodecStanding.Unknown)]
    [InlineData(null, CodecStanding.Unknown)]
    [InlineData("something-new", CodecStanding.Unknown)]
    public void Classify_FollowsTheFieldGuideRecommendation(string? decoder, CodecStanding expected)
    {
        CodecAdvice.Classify(decoder).Should().Be(expected);
    }

    [Theory]
    [InlineData("d3d11h264dec")]
    [InlineData("avdec_prores")]
    [InlineData("")]
    [InlineData("something-new")]
    public void StatusText_IsEmpty_WhenNotWarned(string decoder)
    {
        CodecAdvice.StatusText(decoder).Should().BeEmpty(
            "推奨・可・判定できない形式では警告しない（誤って警告するより安全）");
    }

    [Fact]
    public void StatusText_NamesTheCodecAndTheRecommendation()
    {
        string text = CodecAdvice.StatusText("d3d11vp9dec");

        text.Should().Contain("VP9").And.Contain("推奨外").And.Contain("H.264");
    }

    [Fact]
    public void StatusText_ForAv1_AlsoSaysTheKeyframeIntervalCannotBeChecked()
    {
        CodecAdvice.StatusText("dav1ddec").Should().Contain("AV1").And.Contain("キーフレーム間隔も確認できません",
            "AV1 はキーフレーム間隔を読めないので、長 GOP の警告も出ない。それを伝える");
    }
}
