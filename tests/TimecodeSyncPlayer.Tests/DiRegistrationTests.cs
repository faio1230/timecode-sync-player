using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace TimecodeSyncPlayer.Tests;

public class DiRegistrationTests
{
    [Theory]
    [InlineData(typeof(PixelBufferManager))]
    [InlineData(typeof(GapFreezeHandler))]
    [InlineData(typeof(SpoutFramePublisher))]
    [InlineData(typeof(MpvSessionInitializer))]
    [InlineData(typeof(ProjectLoadApplicator))]
    [InlineData(typeof(StartupBufferInitializer))]
    [InlineData(typeof(RenderedFrameFreezeBufferCopier))]
    [InlineData(typeof(RenderFramePerformanceRecorder))]
    public void ConfigureServices_RegistersCompositionServiceAsSingleton(Type serviceType)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using ServiceProvider provider = services.BuildServiceProvider();

        object first = provider.GetRequiredService(serviceType);
        object second = provider.GetRequiredService(serviceType);

        second.Should().BeSameAs(first);
    }
}
