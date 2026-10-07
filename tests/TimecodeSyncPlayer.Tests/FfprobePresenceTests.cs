using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.6 F-7: 起動時のログの 1 行（ffprobe の有無）。PATH を探すだけで起動はしない。</summary>
public class FfprobePresenceTests
{
    [Fact]
    public void FindOnPath_FindsTheExeInAPathEntry()
    {
        string dir = TestTempPaths.Combine("f7-ffprobe-presence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "ffprobe.exe");
        File.WriteAllBytes(exe, []);
        try
        {
            string path = string.Join(Path.PathSeparator, @"Z:\tcs-no-such-dir", "", $"\"{dir}\"");
            FfprobePresence.FindOnPath(path).Should().Be(exe);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"Z:\tcs-no-such-dir;Z:\tcs-no-such-dir-2")]
    [InlineData("bad|path<>")]
    public void FindOnPath_ReturnsNull_WhenAbsent(string? path)
    {
        FfprobePresence.FindOnPath(path).Should().BeNull();
    }

    [Fact]
    public void Describe_SaysWhetherItWasFound_AndThatItIsNotUsed()
    {
        FfprobePresence.Describe(null).Should().StartWith("ffprobe: not found on PATH").And.Contain("not used");
        FfprobePresence.Describe(@"C:\x\ffprobe.exe").Should().StartWith("ffprobe: found on PATH").And.Contain("not used");
    }
}
