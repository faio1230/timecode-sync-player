namespace TimecodeSyncPlayer.Tests;

public class AccuracyFrameMarkerTests
{
    // Independent, hand checked bytes: DD xor AA xor 12 xor 34 xor 02 = 53.
    internal static byte[] GoldenPixels()
    {
        byte[] pixels = new byte[1920 * 1080 * 4];
        byte[] marker = [0xDD, 0xAA, 0x12, 0x34, 0x02, 0x53];
        for (int bit = 0; bit < 48; bit++)
        {
            byte value = (marker[bit / 8] & (1 << (7 - bit % 8))) != 0 ? (byte)255 : (byte)0;
            int offset = (48 * 1920 + 40 + bit * 16) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
        }
        return pixels;
    }

    [Fact]
    public void GoldenMarker_DecodesFrameAndClipFromPixels()
    {
        var result = AccuracyFrameMarker.Probe(GoldenPixels(), 1920, 1080, 1920 * 4);
        Assert.True(result.MarkerValid);
        Assert.Equal(0x1234, result.FrameIndex);
        Assert.Equal(2, result.ClipId);
        Assert.False(result.IsBlack);
    }

    [Theory]
    [InlineData(47, 255)] // checksum bit should be white: mutate below by XOR
    [InlineData(0, 127)] // neither white nor black
    public void DamagedMarker_IsUnknownInsteadOfInventingAFrame(int bit, byte value)
    {
        var pixels = GoldenPixels();
        int offset = (48 * 1920 + 40 + bit * 16) * 4;
        if (bit == 47) value = 0;
        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
        var result = AccuracyFrameMarker.Probe(pixels, 1920, 1080, 1920 * 4);
        Assert.False(result.MarkerValid);
        Assert.Null(result.FrameIndex);
        Assert.Null(result.ClipId);
        Assert.False(result.IsBlack);
    }

    [Fact]
    public void ShortBuffer_IsNeitherValidMarkerNorConfirmedBlack()
    {
        var result = AccuracyFrameMarker.Probe(new byte[200], 1920, 1080, 1920 * 4);
        Assert.False(result.MarkerValid);
        Assert.False(result.IsBlack);
    }

    [Fact]
    public void BlackDetection_ChecksPixelsAwayFromMissingMarker()
    {
        var pixels = new byte[1920 * 1080 * 4];
        Assert.True(AccuracyFrameMarker.Probe(pixels, 1920, 1080, 1920 * 4).IsBlack);
        pixels[0] = 255;
        var result = AccuracyFrameMarker.Probe(pixels, 1920, 1080, 1920 * 4);
        Assert.False(result.MarkerValid);
        Assert.False(result.IsBlack);
    }
}
