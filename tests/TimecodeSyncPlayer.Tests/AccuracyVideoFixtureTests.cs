using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class AccuracyVideoFixtureTests
{
    [Fact]
    public void MarkerPanel_EncodesIndependentGoldenBytesInEveryCell()
    {
        // DD AA 01 2C 03 59: catches bit-order, frame byte-order and XOR mistakes.
        byte[] panel = AccuracyVideoFixture.CreateMarkerPanel(3, 300);
        byte[] expected = [0xDD, 0xAA, 0x01, 0x2C, 0x03, 0x59];
        Assert.Equal(768 * 32, panel.Length);
        for (int cell = 0; cell < 48; cell++)
        {
            byte value = (expected[cell / 8] & (1 << (7 - cell % 8))) != 0 ? (byte)255 : (byte)0;
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(value, panel[y * 768 + cell * 16 + x]);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 65536)]
    public void MarkerPanel_RejectsUnrepresentableIdentity(int clipId, int frameIndex) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AccuracyVideoFixture.CreateMarkerPanel(clipId, frameIndex));

    [Theory]
    [InlineData(24, 1, 24)]
    [InlineData(30000, 1001, 30)]
    [InlineData(60, 1, 60)]
    public void ResolveKeyframeIntervalFrames_DefaultsToOneSecondRounded(int numerator, int denominator, int expected)
    {
        WithGopOverride(null, () =>
            Assert.Equal(expected, AccuracyVideoFixture.ResolveKeyframeIntervalFrames(numerator, denominator)));
    }

    [Fact]
    public void ResolveKeyframeIntervalFrames_EnvironmentOverrideWins()
    {
        WithGopOverride("250", () =>
            Assert.Equal(250, AccuracyVideoFixture.ResolveKeyframeIntervalFrames(60, 1)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void ResolveKeyframeIntervalFrames_RejectsInvalidOverride(string value)
    {
        WithGopOverride(value, () =>
            Assert.Throws<InvalidOperationException>(() => AccuracyVideoFixture.ResolveKeyframeIntervalFrames(60, 1)));
    }

    private static void WithGopOverride(string? value, Action action)
    {
        string variable = AccuracyVideoFixture.GopOverrideEnvironmentVariable;
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
