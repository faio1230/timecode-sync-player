using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>R1 1-1/1-2: 既定は出荷構成（Gpu）。Gpu が使えなくても落とす先が無いため、再生不可を伝える。</summary>
public class OutputBackendStateTests
{
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
    public void State_BeforeInitialize_IsGpuPlaceholderAndNotInitialized()
    {
        var state = new OutputBackendState();

        state.Effective.Should().Be(OutputBackend.Gpu);
        state.IsInitialized.Should().BeFalse(
            "初期化前の MainWindow 構築で GPU 出力を開始しない");
        state.PlaybackAvailable.Should().BeTrue(
            "単体テストの UI 状態機械は初期化なしで動く（エンジン生成は IsInitialized で止める）");
    }

    [Fact]
    public void State_InitializeWithUnsupportedGpu_StoresUnavailableDecision()
    {
        var state = new OutputBackendState();

        state.Initialize(OutputBackend.Gpu, () => new(false, "detect failed"));

        state.Effective.Should().Be(OutputBackend.Gpu);
        state.Decision.Requested.Should().Be(OutputBackend.Gpu);
        state.PlaybackAvailable.Should().BeFalse();
        state.IsInitialized.Should().BeTrue();
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

    [Fact]
    public void OutputBackend_CpuIsRemovedFromEnum()
    {
        Enum.IsDefined((OutputBackend)0).Should().BeFalse(
            "v0.3 の outputBackend=0（Cpu）は設定互換で読み飛ばす値であり、enum には残さない");
        Enum.GetValues<OutputBackend>().Should().Equal(OutputBackend.Gpu);
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
