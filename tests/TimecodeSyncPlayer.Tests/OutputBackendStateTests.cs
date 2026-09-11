using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class OutputBackendStateTests
{
    [Fact]
    public void Resolve_CpuRequest_DoesNotProbeAndStaysCpu()
    {
        int probeCalls = 0;
        D3D11CapabilityResult Probe()
        {
            probeCalls++;
            return new(true, "unused");
        }

        OutputBackendDecision decision = OutputBackendResolver.Resolve(OutputBackend.Cpu, Probe);

        probeCalls.Should().Be(0);
        decision.Requested.Should().Be(OutputBackend.Cpu);
        decision.Effective.Should().Be(OutputBackend.Cpu);
        decision.FallbackApplied.Should().BeFalse();
    }

    [Fact]
    public void Resolve_GpuRequest_WhenSupported_StaysGpu()
    {
        OutputBackendDecision decision = OutputBackendResolver.Resolve(
            OutputBackend.Gpu,
            () => new(true, "D3D11.4 共有フェンス利用可。"));

        decision.Effective.Should().Be(OutputBackend.Gpu);
        decision.FallbackApplied.Should().BeFalse();
        decision.Detail.Should().Be("D3D11.4 共有フェンス利用可。");
    }

    [Fact]
    public void Resolve_GpuRequest_WhenUnsupported_FallsBackToCpu()
    {
        OutputBackendDecision decision = OutputBackendResolver.Resolve(
            OutputBackend.Gpu,
            () => new(false, "ID3D11Device5 を取得できません（D3D11.4 非対応）。"));

        decision.Requested.Should().Be(OutputBackend.Gpu);
        decision.Effective.Should().Be(OutputBackend.Cpu);
        decision.FallbackApplied.Should().BeTrue();
        decision.Detail.Should().Be("ID3D11Device5 を取得できません（D3D11.4 非対応）。");
    }

    [Fact]
    public void State_BeforeInitialize_DefaultsToCpu()
    {
        var state = new OutputBackendState();

        state.Effective.Should().Be(OutputBackend.Cpu);
        state.Decision.FallbackApplied.Should().BeFalse();
    }

    [Fact]
    public void State_InitializeWithUnsupportedGpu_StoresFallbackDecision()
    {
        var state = new OutputBackendState();

        state.Initialize(OutputBackend.Gpu, () => new(false, "detect failed"));

        state.Effective.Should().Be(OutputBackend.Cpu);
        state.Decision.Requested.Should().Be(OutputBackend.Gpu);
        state.Decision.FallbackApplied.Should().BeTrue();
        state.Decision.Detail.Should().Be("detect failed");
    }
}
