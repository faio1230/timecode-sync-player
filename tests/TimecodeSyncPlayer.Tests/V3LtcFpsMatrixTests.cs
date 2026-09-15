using System.Globalization;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// V3 LTC fps マトリクス（24 / 25 / 29.97 / 30）の入力解釈。
/// 環境変数のトークンを実 fps とアプリの LtcFpsModeCombo インデックスへ写す。
/// </summary>
public class V3LtcFpsMatrixTests
{
    [Theory]
    [InlineData(null, 25.0)]
    [InlineData("", 25.0)]
    [InlineData("24", 24.0)]
    [InlineData("25", 25.0)]
    [InlineData("29.97", 30000.0 / 1001.0)]
    [InlineData("30", 30.0)]
    public void ResolveFps_MapsSupportedTokens(string? token, double expected)
    {
        V3LtcFpsMatrix.ResolveFps(token).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData("23.976")]
    [InlineData("60")]
    [InlineData("29.97002997002997")]
    [InlineData("abc")]
    public void ResolveFps_RejectsUnsupportedTokens(string token)
    {
        FluentActions.Invoking(() => V3LtcFpsMatrix.ResolveFps(token))
            .Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void ResolveFpsModeIndex_DefaultsToAuto(string? token)
    {
        V3LtcFpsMatrix.ResolveFpsModeIndex(token, 25.0).Should().Be(0);
        V3LtcFpsMatrix.ResolveFpsModeIndex(token, 30000.0 / 1001.0).Should().Be(0);
    }

    [Theory]
    [InlineData(24.0, 1)]
    [InlineData(25.0, 2)]
    [InlineData(30000.0 / 1001.0, 3)]
    [InlineData(30.0, 4)]
    public void ResolveFpsModeIndex_FixedMapsToComboIndex(double fps, int expected)
    {
        V3LtcFpsMatrix.ResolveFpsModeIndex("fixed", fps).Should().Be(expected);
    }

    [Fact]
    public void ResolveFpsModeIndex_RejectsUnknownMode()
    {
        FluentActions.Invoking(() => V3LtcFpsMatrix.ResolveFpsModeIndex("Fixed25", 25.0))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FormatReferenceFps_IsInvariantAndRoundTrips29_97()
    {
        string text = V3LtcFpsMatrix.FormatReferenceFps(30000.0 / 1001.0);

        double parsed = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        parsed.Should().BeApproximately(30000.0 / 1001.0, 1e-9);
    }
}
