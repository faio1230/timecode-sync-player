using FluentAssertions;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T6: shim の配信リングイベントの写像。flags bit 3 (8) の position スナップショットを
/// gst.delivery と混ぜず、gst.position として position と最新 delivery PTS を残す。
/// </summary>
public class GstDeliveryTraceMapperTests
{
    private static GstNative.TcsDeliveryEvent Delivery(uint flags = 4) => new()
    {
        Qpc = 1_000,
        Seq = 7,
        PtsNs = 2_000_000,
        RunningNs = 123_456,
        CallbackUs = 15,
        Flags = flags,
    };

    [Fact]
    public void Map_Arrival_KeepsGstDeliveryShape()
    {
        OutputTraceEvent mapped = GstDeliveryTraceMapper.Map(Delivery());

        mapped.Stage.Should().Be("gst.delivery");
        mapped.Worker.Should().Be("GST");
        mapped.Qpc.Should().Be(1_000);
        mapped.ImageId.Should().Be(7);
        mapped.Detail.Should().Be("2000000:123456:4");
        mapped.Value.Should().Be(15);
        mapped.PtsNs.Should().Be(0);
    }

    [Fact]
    public void Map_PositionSnapshot_UsesGstPosition()
    {
        GstNative.TcsDeliveryEvent e = Delivery(flags: GstDeliveryTraceMapper.PositionFlag);
        e.PtsNs = 2_000_000;
        e.RunningNs = 1_500_000_000;

        OutputTraceEvent mapped = GstDeliveryTraceMapper.Map(e);

        mapped.Stage.Should().Be("gst.position");
        mapped.Qpc.Should().Be(1_000);
        mapped.ImageId.Should().Be(7);
        mapped.PtsNs.Should().Be(2_000_000);
        mapped.Value.Should().Be(1_500_000);
        mapped.Detail.Should().BeNull();
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(4u)]
    public void Map_FlagWithoutPositionBit_StaysDelivery(uint flags)
        => GstDeliveryTraceMapper.Map(Delivery(flags)).Stage.Should().Be("gst.delivery");

    [Fact]
    public void Map_PositionBitWithOtherBits_StaysPosition()
        => GstDeliveryTraceMapper.Map(Delivery(flags: GstDeliveryTraceMapper.PositionFlag | 4u))
            .Stage.Should().Be("gst.position");

    [Fact]
    public void Map_PositionFallback_UsesDedicatedStage()
    {
        GstNative.TcsDeliveryEvent e = Delivery(
            flags: GstDeliveryTraceMapper.PositionFlag | GstDeliveryTraceMapper.PositionFallbackFlag);
        e.PtsNs = 2_000_000;
        e.RunningNs = 1_500_000_000;

        OutputTraceEvent mapped = GstDeliveryTraceMapper.Map(e);

        mapped.Stage.Should().Be("gst.positionFallback");
        mapped.Qpc.Should().Be(1_000);
        mapped.ImageId.Should().Be(7);
        mapped.PtsNs.Should().Be(2_000_000);
        mapped.Value.Should().Be(1_500_000);
    }

    [Fact]
    public void Map_RejectedPositionFallback_CountsAsItsOwnStage()
    {
        // 0.4.5-A フェーズ 2: 旧世代の PTS を返さなかった回数を events.jsonl で数えられる。
        GstNative.TcsDeliveryEvent e = Delivery(
            flags: GstDeliveryTraceMapper.PositionFlag |
                   GstDeliveryTraceMapper.PositionFallbackFlag |
                   GstDeliveryTraceMapper.PositionFallbackRejectedFlag);
        e.PtsNs = 2_000_000;
        e.RunningNs = 0;

        OutputTraceEvent mapped = GstDeliveryTraceMapper.Map(e);

        mapped.Stage.Should().Be("gst.positionFallbackRejected");
        mapped.Qpc.Should().Be(1_000);
        mapped.ImageId.Should().Be(7);
        mapped.PtsNs.Should().Be(2_000_000);
        mapped.Value.Should().Be(0, "返した位置は無い");
    }
}
