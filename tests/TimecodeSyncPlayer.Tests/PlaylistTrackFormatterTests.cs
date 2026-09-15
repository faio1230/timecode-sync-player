using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistTrackFormatterTests
{
    [Fact]
    public void FormatTimecode_FormatsCorrectly()
    {
        var ts = TimeSpan.FromSeconds(90.5);
        var result = PlaylistTrackFormatter.FormatTimecode(ts, 30);
        result.Should().Be("00:01:30:15");
    }

    [Fact]
    public void FormatTimecode_Zero_ReturnsZeros()
    {
        var ts = TimeSpan.Zero;
        var result = PlaylistTrackFormatter.FormatTimecode(ts, 30);
        result.Should().Be("00:00:00:00");
    }

    [Fact]
    public void FormatTimecode_Hours_ReturnsCorrectHours()
    {
        var ts = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(30);
        var result = PlaylistTrackFormatter.FormatTimecode(ts, 30);
        result.Should().Be("01:30:00:00");
    }

    [Theory]
    [InlineData("00:00:30:00", 30, 30.0)]
    [InlineData("00:01:00:00", 30, 60.0)]
    [InlineData("00:00:00:15", 30, 0.5)]
    [InlineData("01:00:00:00", 30, 3600.0)]
    [InlineData("00:00:00:00", 30, 0.0)]
    [InlineData("00:00:59:29", 30, 59.0 + 29.0 / 30.0)]
    [InlineData("99:59:59:23", 24, 99 * 3600.0 + 59 * 60.0 + 59.0 + 23.0 / 24.0)]
    public void TryParseTimecode_InRangeInputs_ReturnSameValueWithoutAdjustment(
        string input, int fps, double expectedSeconds)
    {
        var success = PlaylistTrackFormatter.TryParseTimecode(input, fps, out var result, out bool adjusted);

        success.Should().BeTrue();
        result.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);
        adjusted.Should().BeFalse("範囲内の入力は現行と同じ");
    }

    [Theory]
    [InlineData("00:00:00:55", 30, 29)]
    [InlineData("00:00:00:30", 30, 29)]
    [InlineData("00:00:00:24", 24, 23)]
    [InlineData("00:00:00:60", 60, 59)]
    [InlineData("00:00:00:-3", 30, 0)]
    public void TryParseTimecode_ClampsFramesIntoRange_AndReportsAdjustment(
        string input, int fps, int expectedFrames)
    {
        var success = PlaylistTrackFormatter.TryParseTimecode(input, fps, out var result, out bool adjusted);

        success.Should().BeTrue("範囲外の ff は拒否せず丸め込む");
        adjusted.Should().BeTrue();
        result.TotalSeconds.Should().BeApproximately((double)expectedFrames / fps, 0.000001);
        result.TotalSeconds.Should().BeLessThan(1.0, "ff の丸め込みは秒へ繰り上がらない（fps-1 は 1 秒未満）");
    }

    [Theory]
    [InlineData("00:00:75:00", 75.0)]
    [InlineData("00:75:00:00", 75 * 60.0)]
    [InlineData("01:90:90:00", 3600.0 + 90 * 60.0 + 90.0)]
    public void TryParseTimecode_CarriesSecondsAndMinutes_AndReportsAdjustment(
        string input, double expectedSeconds)
    {
        var success = PlaylistTrackFormatter.TryParseTimecode(input, 30, out var result, out bool adjusted);

        success.Should().BeTrue("ss/mm は拒否せず上の桁へ繰り上げる");
        adjusted.Should().BeTrue();
        result.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);
    }

    [Fact]
    public void TryParseTimecode_SemicolonSeparator_IsEquivalentToColon()
    {
        PlaylistTrackFormatter.TryParseTimecode("00:00:00:55", 30, out var colonValue, out bool colonAdjusted)
            .Should().BeTrue();
        PlaylistTrackFormatter.TryParseTimecode("00:00:00;55", 30, out var semicolonValue, out bool semicolonAdjusted)
            .Should().BeTrue("現場の慣習の ; 区切りも入力として受け付ける");

        semicolonValue.Should().Be(colonValue);
        semicolonAdjusted.Should().Be(colonAdjusted);

        PlaylistTrackFormatter.TryParseTimecode("00:00:00;15", 30, out var inRange, out bool inRangeAdjusted)
            .Should().BeTrue();
        inRange.TotalSeconds.Should().BeApproximately(0.5, 0.001);
        inRangeAdjusted.Should().BeFalse("区切り文字は値の変更ではない");
    }

    [Theory]
    [InlineData("")]
    [InlineData("00:00:00")]
    [InlineData("00:00:00:00:00")]
    [InlineData("invalid")]
    [InlineData("-01:00:00:00")]
    [InlineData("00:00:-1:00")]
    [InlineData("00:-1:00:00")]
    [InlineData("100:00:00:00")]
    [InlineData("99:60:00:00")]
    public void TryParseTimecode_InvalidInputs_ReturnsFalse(string input)
    {
        var success = PlaylistTrackFormatter.TryParseTimecode(input, 30, out _, out bool adjusted);

        success.Should().BeFalse();
        adjusted.Should().BeFalse();
    }

    [Fact]
    public void TryParseTimecode_NonPositiveFps_ReturnsFalse()
    {
        PlaylistTrackFormatter.TryParseTimecode("00:00:00:00", 0, out _, out _).Should().BeFalse();
        PlaylistTrackFormatter.TryParseTimecode("00:00:00:00", -30, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void FormatTimelineOffset_UsesTrackFrameRate()
    {
        var track = CreateTrack(timelineOffset: TimeSpan.FromSeconds(10.5), frameRate: 24);
        var result = PlaylistTrackFormatter.FormatTimelineOffset(track);
        result.Should().Be("00:00:10:12");
    }

    [Fact]
    public void FormatMediaDuration_UsesTrackFrameRate()
    {
        var track = CreateTrack(mediaDuration: TimeSpan.FromSeconds(120), frameRate: 25);
        var result = PlaylistTrackFormatter.FormatMediaDuration(track);
        result.Should().Be("00:02:00:00");
    }

    [Fact]
    public void FormatEffectiveDuration_UsesTrackFrameRate()
    {
        var track = CreateTrack(mediaIn: TimeSpan.FromSeconds(10), mediaDuration: TimeSpan.FromSeconds(70), frameRate: 30);
        var result = PlaylistTrackFormatter.FormatEffectiveDuration(track);
        result.Should().Be("00:01:00:00");
    }

    [Fact]
    public void FormatTimelineRange_UsesActualPositions()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromSeconds(10),
            mediaDuration: TimeSpan.FromSeconds(60),
            frameRate: 30);

        var result = PlaylistTrackFormatter.FormatTimelineRange(track);
        result.Should().Be("00:00:10:00 → 00:01:10:00");
    }

    private static PlaylistTrack CreateTrack(
        TimeSpan? timelineOffset = null,
        TimeSpan? mediaDuration = null,
        TimeSpan? mediaIn = null,
        double? frameRate = null)
    {
        return new PlaylistTrack(
            Id: Guid.NewGuid(),
            FilePath: "test.mp4",
            Name: "test",
            MediaIn: mediaIn ?? TimeSpan.Zero,
            MediaOut: null,
            TimelineOffset: timelineOffset ?? TimeSpan.Zero,
            MediaDuration: mediaDuration ?? TimeSpan.Zero,
            SyncOffset: TimeSpan.Zero,
            FrameRate: frameRate ?? 30,
            IsEnabled: true);
    }
}
