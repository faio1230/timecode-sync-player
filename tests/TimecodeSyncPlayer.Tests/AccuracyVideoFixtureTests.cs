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
}
