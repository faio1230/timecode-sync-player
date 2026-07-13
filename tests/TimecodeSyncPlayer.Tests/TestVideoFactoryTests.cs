using System.IO;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class TestVideoFactoryTests
{
    [SkippableFact]
    public void GetOrCreateVariant_CreatesStableDistinctPath()
    {
        Skip.IfNot(TestVideoFactory.FfmpegAvailable(), "ffmpeg is not available on PATH");

        string first = TestVideoFactory.GetOrCreate();
        string second = TestVideoFactory.GetOrCreateVariant("alt");

        second.Should().NotBe(first);
        File.Exists(second).Should().BeTrue();
        TestVideoFactory.GetOrCreateVariant("alt").Should().Be(second);
    }
}
