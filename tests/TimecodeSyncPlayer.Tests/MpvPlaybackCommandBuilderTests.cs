using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class MpvPlaybackCommandBuilderTests
{
    [Fact]
    public void BuildLoadFileCommand_UsesNoOsdLoadfileReplace()
    {
        string command = MpvPlaybackCommandBuilder.BuildLoadFileCommand(
            @"C:\media\clip.mp4",
            startPosition: null);

        command.Should().Be("no-osd loadfile \"C:/media/clip.mp4\" replace");
    }

    [Fact]
    public void BuildLoadFileCommand_AddsStartPosition()
    {
        string command = MpvPlaybackCommandBuilder.BuildLoadFileCommand(
            @"C:\media\clip.mp4",
            startPosition: 12.3456789);

        command.Should().Be("no-osd loadfile \"C:/media/clip.mp4\" replace -1 start=12.345679");
    }

    [Fact]
    public void BuildLoadFileCommand_EscapesQuotes()
    {
        string command = MpvPlaybackCommandBuilder.BuildLoadFileCommand(
            "C:/media/a\"b.mp4",
            startPosition: null);

        command.Should().Be("no-osd loadfile \"C:/media/a\\\"b.mp4\" replace");
    }
}
