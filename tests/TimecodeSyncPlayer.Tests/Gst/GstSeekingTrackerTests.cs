using FluentAssertions;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.Gst;

/// <summary>
/// D11: シーク保留（seeking）の解除規則。
/// 通常は到着数の増加で解除し、EOF 後は Ended 通知、安全網として発行から 2 秒で解除する。
/// </summary>
public class GstSeekingTrackerTests
{
    [Fact]
    public void IsSeeking_NewArrivalClearsPending()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1), DeliveryArrivals = 10 };
        var state = new GstBackendState(native);
        state.EnsurePlayer().Should().BeTrue();
        state.Seeking.MarkPending(state.Seeking.ReadArrivalBaseline(state.Player));

        state.Seeking.IsSeeking(state.Player).Should().BeTrue();

        native.DeliveryArrivals = 11;
        state.Seeking.IsSeeking(state.Player).Should().BeFalse();
    }

    [Fact]
    public void IsSeeking_ClearsAfterTwoSecondsWithoutArrival()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1), DeliveryArrivals = 10 };
        var state = new GstBackendState(native, settingsManager: null, timeProvider: clock);
        state.EnsurePlayer().Should().BeTrue();
        state.Seeking.MarkPending(10);

        clock.Advance(TimeSpan.FromSeconds(1.9));
        state.Seeking.IsSeeking(state.Player).Should().BeTrue("2 秒までは到着を待つ");

        clock.Advance(TimeSpan.FromSeconds(0.1));
        state.Seeking.IsSeeking(state.Player).Should().BeFalse("安全網: 発行から 2 秒で解除する");

        // 解除後は時間が進んでも true に戻らない。
        clock.Advance(TimeSpan.FromSeconds(5));
        state.Seeking.IsSeeking(state.Player).Should().BeFalse();
    }

    [Fact]
    public void IsSeeking_EndedNotificationClearsPending()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1), DeliveryArrivals = 10 };
        var state = new GstBackendState(native);
        state.EnsurePlayer().Should().BeTrue();
        state.Seeking.MarkPending(10);
        state.Seeking.IsSeeking(state.Player).Should().BeTrue();

        state.Seeking.NotifyEnded();

        state.Seeking.IsSeeking(state.Player).Should().BeFalse("EOF 後は新しい配信が来ないため Ended で解除する");
    }
}
