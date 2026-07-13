using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class MetadataDisplayFormatterTests
{
    [Fact]
    public void FormatMetadataLine_IncludesResolutionFpsAndCodecs()
    {
        string result = MetadataDisplayFormatter.FormatMetadataLine(
            width: 1920,
            height: 1080,
            fps: 60.0,
            videoCodec: "h264",
            audioCodec: "aac");

        result.Should().Be("1920x1080  60.000 fps  V:h264  A:aac");
    }

    [Fact]
    public void FormatMetadataLine_OmitsUnavailableParts()
    {
        string result = MetadataDisplayFormatter.FormatMetadataLine(
            width: 0,
            height: 1080,
            fps: 0.0,
            videoCodec: "",
            audioCodec: "opus");

        result.Should().Be("A:opus");
    }

    [Fact]
    public void FormatMetadataLine_ReturnsEmpty_WhenAllPartsUnavailable()
    {
        string result = MetadataDisplayFormatter.FormatMetadataLine(
            width: 0,
            height: 0,
            fps: 0.0,
            videoCodec: "",
            audioCodec: "");

        result.Should().BeEmpty();
    }

    [Fact]
    public void FormatOsdText_AppendsMetadataOnNextLine()
    {
        string result = MetadataDisplayFormatter.FormatOsdText(
            timeText: "0:00:12:15",
            metadataLine: "1920x1080  60.000 fps");

        result.Should().Be("0:00:12:15\n1920x1080  60.000 fps");
    }

    [Fact]
    public void FormatOsdText_ReturnsTimeOnly_WhenMetadataIsEmpty()
    {
        string result = MetadataDisplayFormatter.FormatOsdText(
            timeText: "0:00:12:15",
            metadataLine: "");

        result.Should().Be("0:00:12:15");
    }
}
