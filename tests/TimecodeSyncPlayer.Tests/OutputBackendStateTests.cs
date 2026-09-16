using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>R1 1-1/1-2: 既定は出荷構成（Gpu）。Gpu が使えなくても Cpu へ黙って落とさず、再生不可を伝える。</summary>
public class OutputBackendStateTests
{
    [Fact]
    public void Resolve_CpuRequest_DoesNotProbeAndStaysPlayableCpu()
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
        decision.PlaybackAvailable.Should().BeTrue();
    }

    [Fact]
    public void Resolve_GpuRequest_WhenSupported_StaysGpuAndPlayable()
    {
        OutputBackendDecision decision = OutputBackendResolver.Resolve(
            OutputBackend.Gpu,
            () => new(true, "D3D11.4 共有フェンス利用可"));

        decision.Effective.Should().Be(OutputBackend.Gpu);
        decision.PlaybackAvailable.Should().BeTrue();
        decision.Detail.Should().Be("D3D11.4 共有フェンス利用可");
    }

    [Fact]
    public void Resolve_GpuRequest_WhenUnsupported_StaysGpuAndDisablesPlayback()
    {
        OutputBackendDecision decision = OutputBackendResolver.Resolve(
            OutputBackend.Gpu,
            () => new(false, "ID3D11Device5 を取得できません（D3D11.4 非対応）"));

        decision.Requested.Should().Be(OutputBackend.Gpu);
        decision.Effective.Should().Be(OutputBackend.Gpu);
        decision.PlaybackAvailable.Should().BeFalse();
        decision.Detail.Should().Be("ID3D11Device5 を取得できません（D3D11.4 非対応）");
    }

    [Fact]
    public void State_BeforeInitialize_UsesCpuPlaceholder()
    {
        var state = new OutputBackendState();

        state.Effective.Should().Be(OutputBackend.Cpu);
        state.PlaybackAvailable.Should().BeTrue();
    }

    [Fact]
    public void State_InitializeWithUnsupportedGpu_StoresUnavailableDecision()
    {
        var state = new OutputBackendState();

        state.Initialize(OutputBackend.Gpu, () => new(false, "detect failed"));

        state.Effective.Should().Be(OutputBackend.Gpu);
        state.Decision.Requested.Should().Be(OutputBackend.Gpu);
        state.PlaybackAvailable.Should().BeFalse();
        state.Decision.Detail.Should().Be("detect failed");
    }

    [Fact]
    public void State_InitializeWithForcedUnavailable_SkipsProbeAndDisablesPlayback()
    {
        var state = new OutputBackendState();
        int probeCalls = 0;

        state.Initialize(OutputBackend.Gpu, () => { probeCalls++; return new(true, "unused"); }, forceUnavailable: true);

        probeCalls.Should().Be(0);
        state.PlaybackAvailable.Should().BeFalse();
        state.Decision.Detail.Should().Contain(OutputBackendState.ForceUnavailableEnvironmentVariable);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    public void IsForcedUnavailable_ParsesInjectionValues(string? value, bool expected)
    {
        OutputBackendState.IsForcedUnavailable(value).Should().Be(expected);
    }
}
