using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.E2E;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class Qa002UiaResponsivenessE2ETests
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(1500);
    private readonly ITestOutputHelper _output;

    public Qa002UiaResponsivenessE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public void FullTreeEnumeration_CompletesWhileVideoIsPlaying()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(skipReason != null, skipReason);

        string videoPath = TestVideoFactory.GetOrCreate();
        using E2EAppRunner app = E2EAppRunner.Start(
            exePath,
            $"--open \"{videoPath}\" --playlist \"{videoPath}\"",
            settingsFilePath: null,
            pausePlaybackIfNeeded: false);

        E2EAssert.WaitUntil(() => app.Button("BtnPlay").Name == "⏸", TimeSpan.FromSeconds(2));
        Thread.Sleep(2000);

        app.Process.Refresh();
        long started = Stopwatch.GetTimestamp();
        AutomationElement[] descendants = app.MainWindow.FindAllDescendants();
        TreeQueryResult result = new(
            descendants.Length + 1,
            Stopwatch.GetElapsedTime(started));
        _output.WriteLine(
            $"QA-002 GREEN: elements={result.ElementCount}, elapsedMs={result.Elapsed.TotalMilliseconds:F2}");
        result.ElementCount.Should().BeGreaterThanOrEqualTo(80);
        result.Elapsed.Should().BeLessThan(QueryTimeout);
    }

    private sealed record TreeQueryResult(int ElementCount, TimeSpan Elapsed);
}
