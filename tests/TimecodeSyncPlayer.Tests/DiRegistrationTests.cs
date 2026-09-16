using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class DiRegistrationTests
{
    [Theory]
    [InlineData(typeof(IRenderUpdateScheduler))]
    public void ConfigureServices_RenderSessionOwnsRenderingHelpers(Type serviceType)
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        services.Should().NotContain(descriptor => descriptor.ServiceType == serviceType);
    }

    [Theory]
    [InlineData(typeof(GapFreezeHandler))]
    [InlineData(typeof(MpvSessionInitializer))]
    [InlineData(typeof(ProjectLoadApplicator))]
    [InlineData(typeof(IPlaybackApi))]
    [InlineData(typeof(IRenderUpdateSource))]
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
