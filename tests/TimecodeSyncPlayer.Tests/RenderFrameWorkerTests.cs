using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class RenderFrameWorkerTests
{
    [Fact]
    public void Execute_RendersWithoutUiDisplayOrSpoutDependencies()
    {
        var calls = new List<string>();
        IntPtr pixels = new(1234);
        var worker = new RenderFrameWorker(
            ensurePixelBuffer: (width, height) => calls.Add($"ensure:{width}x{height}"),
            buildRenderParameters: (width, height) =>
            {
                calls.Add($"build:{width}x{height}");
                return pixels;
            },
            renderFrame: () =>
            {
                calls.Add("render");
                return new MpvRenderFrameResult(0, 31.5);
            },
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: code => calls.Add($"log:{code}"));

        RenderFrameWorkerResult result = worker.Execute(
            new RenderFrameSizeDecision(1280, 720, HasDisplayableVideoSize: true));

        calls.Should().Equal(
            "ensure:1280x720",
            "build:1280x720",
            "render");
        result.Should().Be(new RenderFrameWorkerResult(
            ShouldPublish: true,
            Pixels: pixels,
            Width: 1280,
            Height: 720,
            RenderMs: 31.5));
    }

    [Fact]
    public void Execute_RenderFailureLogsAndReturnsNonPublishResult()
    {
        var logCodes = new List<int>();
        var worker = new RenderFrameWorker(
            ensurePixelBuffer: (_, _) => { },
            buildRenderParameters: (_, _) => IntPtr.Zero,
            renderFrame: () => new MpvRenderFrameResult(-7, 0.3),
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: logCodes.Add);

        RenderFrameWorkerResult result = worker.Execute(
            new RenderFrameSizeDecision(320, 180, HasDisplayableVideoSize: true));

        logCodes.Should().Equal(-7);
        result.Should().Be(RenderFrameWorkerResult.NotPublished);
    }

    [Fact]
    public void Execute_InvalidSizeSkipsAllWork()
    {
        var calls = new List<string>();
        var worker = new RenderFrameWorker(
            ensurePixelBuffer: (_, _) => calls.Add("ensure"),
            buildRenderParameters: (_, _) =>
            {
                calls.Add("build");
                return IntPtr.Zero;
            },
            renderFrame: () =>
            {
                calls.Add("render");
                return new MpvRenderFrameResult(0, 0);
            },
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: _ => calls.Add("log"));

        RenderFrameWorkerResult result = worker.Execute(
            new RenderFrameSizeDecision(16, 16, HasDisplayableVideoSize: false, ShouldRender: false));

        calls.Should().BeEmpty();
        result.Should().Be(RenderFrameWorkerResult.NotPublished);
    }
}
