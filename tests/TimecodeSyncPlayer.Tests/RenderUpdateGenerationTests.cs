using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class RenderUpdateGenerationTests
{
    [Fact]
    public void Advance_InvalidatesPreviouslyCapturedGeneration()
    {
        var generation = new RenderUpdateGeneration();
        int previous = generation.Capture();

        generation.Advance();

        generation.IsCurrent(previous).Should().BeFalse();
        generation.IsCurrent(generation.Capture()).Should().BeTrue();
    }
}
