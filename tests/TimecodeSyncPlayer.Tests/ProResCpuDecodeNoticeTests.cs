using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.0: ProRes を CPU で開いたときのアプリのログ（設計書 5 節）。</summary>
public class ProResCpuDecodeNoticeTests
{
    [Fact]
    public void CpuDecoder_LogsTheSettingValue()
    {
        var notice = new ProResCpuDecodeNotice();

        notice.Next("avdec_prores", ProResGpuMode.Auto)
            .Should().Be("ProRes: CPU で復号（proResGpu=auto、理由は tcs-gst のログ）");
    }

    [Fact]
    public void SameContent_IsLoggedOncePerLaunch()
    {
        // FetchMetadata はロードのたび（サイズが取れるまでは tick ごと）に呼ばれる。
        var notice = new ProResCpuDecodeNotice();

        notice.Next("avdec_prores", ProResGpuMode.Off).Should().NotBeNull();
        notice.Next("avdec_prores", ProResGpuMode.Off).Should().BeNull();
        notice.Next("AVDEC_PRORES", ProResGpuMode.Off).Should().BeNull("大文字小文字は同じデコーダ");
    }

    [Fact]
    public void DifferentContent_IsLoggedSeparately()
    {
        var notice = new ProResCpuDecodeNotice();

        notice.Next("avdec_prores", ProResGpuMode.Auto).Should().Contain("proResGpu=auto");
        notice.Next("avdec_prores", ProResGpuMode.On).Should().Contain("proResGpu=on");
    }

    [Theory]
    [InlineData("proresd3d11dec")]
    [InlineData("d3d11h264dec")]
    [InlineData("avdec_h264")]
    [InlineData("hapdec")]
    [InlineData("decodebin(sysmem)")]
    [InlineData("")]
    [InlineData(null)]
    public void GpuProResAndOtherCodecs_AreNotLogged(string? decoderName)
    {
        var notice = new ProResCpuDecodeNotice();

        notice.Next(decoderName, ProResGpuMode.Auto).Should().BeNull();
        notice.Next(decoderName, ProResGpuMode.On).Should().BeNull();
        notice.Next(decoderName, ProResGpuMode.Off).Should().BeNull();
    }

    [Fact]
    public void GpuLoad_DoesNotConsumeTheOneTimeCpuLine()
    {
        // GPU で開いた後に CPU へ落ちた（last-good で CPU に留まる）ときも、最初の CPU の 1 行は出る。
        var notice = new ProResCpuDecodeNotice();

        notice.Next("proresd3d11dec", ProResGpuMode.Auto).Should().BeNull();
        notice.Next("avdec_prores", ProResGpuMode.Auto).Should().NotBeNull();
    }
}
