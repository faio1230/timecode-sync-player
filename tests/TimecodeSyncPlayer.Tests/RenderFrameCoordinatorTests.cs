using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderFrameCoordinatorTests
{
    [Fact]
    public void Render_PublishesFrame_WhenRenderSucceedsAndVideoSizeIsDisplayable()
    {
        var calls = new List<string>();
        IntPtr pixelPointer = new(1234);
        (IntPtr PixelPointer, int Width, int Height, double ElapsedMs)? published = null;

        var coordinator = new RenderFrameCoordinator(
            decideSize: () =>
            {
                calls.Add("decide-size");
                return new RenderFrameSizeDecision(1920, 1080, HasDisplayableVideoSize: true);
            },
            ensurePixelBuffer: (width, height) => calls.Add($"ensure:{width}x{height}"),
            buildRenderParameters: (width, height) =>
            {
                calls.Add($"build:{width}x{height}");
                return pixelPointer;
            },
            renderFrame: () =>
            {
                calls.Add("render");
                return new MpvRenderFrameResult(ReturnCode: 0, ElapsedMs: 1.5);
            },
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: rc => calls.Add($"log:{rc}"),
            publishFrame: (ptr, width, height, elapsedMs) =>
            {
                calls.Add("publish");
                published = (ptr, width, height, elapsedMs);
            });

        coordinator.Render();

        calls.Should().Equal(
            "decide-size",
            "ensure:1920x1080",
            "build:1920x1080",
            "render",
            "publish");
        published.Should().Be((pixelPointer, 1920, 1080, 1.5));
    }

    [Fact]
    public void Render_LogsAndSkipsPublish_WhenRenderFails()
    {
        var logCodes = new List<int>();
        bool published = false;

        var coordinator = new RenderFrameCoordinator(
            decideSize: () => new RenderFrameSizeDecision(1280, 720, HasDisplayableVideoSize: true),
            ensurePixelBuffer: (_, _) => { },
            buildRenderParameters: (_, _) => IntPtr.Zero,
            renderFrame: () => new MpvRenderFrameResult(ReturnCode: -7, ElapsedMs: 0.4),
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: logCodes.Add,
            publishFrame: (_, _, _, _) => published = true);

        coordinator.Render();

        logCodes.Should().Equal(-7);
        published.Should().BeFalse();
    }

    [Fact]
    public void Render_SkipsPublishWithoutLogging_WhenVideoSizeIsNotDisplayable()
    {
        bool logged = false;
        bool published = false;

        var coordinator = new RenderFrameCoordinator(
            decideSize: () => new RenderFrameSizeDecision(16, 16, HasDisplayableVideoSize: false),
            ensurePixelBuffer: (_, _) => { },
            buildRenderParameters: (_, _) => IntPtr.Zero,
            renderFrame: () => new MpvRenderFrameResult(ReturnCode: 0, ElapsedMs: 0.2),
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: _ => logged = true,
            publishFrame: (_, _, _, _) => published = true);

        coordinator.Render();

        logged.Should().BeFalse();
        published.Should().BeFalse();
    }

    [Fact]
    public void Render_InvalidSizeStopsBeforeBufferAndNativeOperations()
    {
        var calls = new List<string>();
        var coordinator = new RenderFrameCoordinator(
            decideSize: () => new RenderFrameSizeDecision(
                16,
                16,
                HasDisplayableVideoSize: false,
                ShouldRender: false),
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
            logRenderFailure: _ => calls.Add("log"),
            publishFrame: (_, _, _, _) => calls.Add("publish"));

        coordinator.Render();

        calls.Should().BeEmpty();
    }
}
