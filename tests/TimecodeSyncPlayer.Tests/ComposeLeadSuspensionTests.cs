using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>L-3: 世代変更・ソース接続・共有リング作成後の lead 学習除外。</summary>
public class ComposeLeadSuspensionTests
{
    private const long Frequency = 1_000_000; // 1 tick = 1 µs.

    public enum SourceEvent { Generation, Attach, Ring }

    [Theory]
    [InlineData(SourceEvent.Generation)]
    [InlineData(SourceEvent.Attach)]
    [InlineData(SourceEvent.Ring)]
    public void SourceEvent_SuspendsLearningForOneSecond(SourceEvent sourceEvent)
    {
        long now = 0;
        var controller = new ComposeLeadController(Frequency, 5);
        var suspension = new ComposeLeadSuspension(() => now);
        suspension.Attach(controller);

        _ = controller.Add(0, now, out _); // 起動後 3 秒の除外を開始する。
        now += (long)(ComposeLeadController.WarmupSeconds * Frequency) + 1;

        switch (sourceEvent)
        {
            case SourceEvent.Generation:
                suspension.OnSourceGenerationChanged();
                break;
            case SourceEvent.Attach:
                suspension.OnSourceAttached();
                break;
            case SourceEvent.Ring:
                suspension.OnSharedRingOpened();
                break;
        }

        // 除外中（1 秒以内）の遅い標本は学習しない。
        for (int i = 0; i < 6; i++)
            _ = controller.Add(50_000, now + i * 80_000, out _);
        controller.CurrentLeadMs.Should().Be(5);

        // 除外明けは遅い窓で即時上げる。
        now += Frequency;
        FeedWindow(controller, 50_000, ref now);
        controller.CurrentLeadMs.Should().Be(8);
    }

    private static void FeedWindow(ComposeLeadController controller, long durationTicks, ref long now)
    {
        for (int i = 0; i < 13; i++)
        {
            _ = controller.Add(durationTicks, now, out _);
            now += 80_000;
        }
        _ = controller.Add(durationTicks, now, out _);
    }
}
