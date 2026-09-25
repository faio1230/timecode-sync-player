using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.2 段 2c: <see cref="SeekLandingWindow"/>（D37-b2 / D37-d / D37-e / D37-f / D37-g）の状態遷移。
/// 開く・発生元を守る・追従開始を終える・上限（回数／時間）で閉じる・到着で閉じる・
/// 前進なしで閉じる、を 1 件ずつ確かめる。
/// </summary>
public class SeekLandingWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

    private static (SeekLandingWindow Window, ManualTimeProvider Clock) Create()
    {
        var clock = new ManualTimeProvider(T0);
        return (new SeekLandingWindow(clock), clock);
    }

    [Fact]
    public void NotifyLanding_OpensWindow()
    {
        (SeekLandingWindow window, _) = Create();

        window.NotifyLanding(LandingOrigin.Other);

        window.IsOpen.Should().BeTrue();
        window.IsActive().Should().BeTrue();
        window.Origin.Should().Be(LandingOrigin.Other);
        window.AwaitingObservation.Should().BeFalse();
    }

    [Fact]
    public void OpenAt_UsesTheGivenTimestamp()
    {
        (SeekLandingWindow window, ManualTimeProvider clock) = Create();

        // 呼び出し側が取った時刻で開く（時計は進んでいない）。4.9 秒前で開いたので、
        // 0.2 秒進めた時点で 5 秒の上限に達している。
        window.OpenAt(T0.UtcDateTime.AddSeconds(-4.9), LandingOrigin.Other);
        clock.Advance(TimeSpan.FromMilliseconds(200));

        window.IsActive().Should().BeFalse();
        window.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Open_KeepsFollowStartOrigin_WhenOtherComesNext()
    {
        // D37-f: 追従開始の窓が生きている間は、Other で発生元を上書きしない
        // （同じフレームでロードが成立しても先行量を失わない）。
        (SeekLandingWindow window, _) = Create();
        window.NotifyLanding(LandingOrigin.FollowStart);

        window.NotifyLanding(LandingOrigin.Other);

        window.Origin.Should().Be(LandingOrigin.FollowStart);
        window.IsActive().Should().BeTrue();

        // 窓が閉じた後の新しい着地は、発生元を普通に受け取る。
        window.ObserveArrival();
        window.NotifyLanding(LandingOrigin.Other);
        window.Origin.Should().Be(LandingOrigin.Other);
    }

    [Fact]
    public void EndFollowStartLanding_EndsEpisodeButKeepsWindow()
    {
        // D37-g: 先行量だけを外し、着地窓（シーク優先）は残す。
        (SeekLandingWindow window, _) = Create();
        window.NotifyLanding(LandingOrigin.FollowStart);

        window.EndFollowStartLanding("boundary hold released");

        window.Origin.Should().Be(LandingOrigin.Other);
        window.IsActive().Should().BeTrue();
    }

    [Fact]
    public void EndFollowStartLanding_DoesNothing_WhenOriginIsOther()
    {
        (SeekLandingWindow window, _) = Create();
        window.NotifyLanding(LandingOrigin.Other);

        window.EndFollowStartLanding("ltc jump");

        window.Origin.Should().Be(LandingOrigin.Other);
    }

    [Fact]
    public void IsActive_ClosesAtTheSeekCap()
    {
        (SeekLandingWindow window, _) = Create();
        window.NotifyLanding(LandingOrigin.Other);

        window.OnSeekSent();
        window.IsActive().Should().BeTrue("1 回目は窓が開いている");
        window.OnSeekSent();
        window.IsActive().Should().BeTrue("2 回目は窓が開いている");
        window.OnSeekSent();
        window.IsActive().Should().BeFalse("3 回目のシークで窓を閉じる");
        window.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void IsActive_ClosesAtTheAgeCap()
    {
        (SeekLandingWindow window, ManualTimeProvider clock) = Create();
        window.NotifyLanding(LandingOrigin.Other);

        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        window.IsActive().Should().BeTrue("5 秒未満は開いている");

        clock.Advance(TimeSpan.FromTicks(1));
        window.IsActive().Should().BeFalse("5 秒に達したら閉じる");
        window.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void ObserveArrival_ClosesWindow()
    {
        (SeekLandingWindow window, _) = Create();
        window.NotifyLanding(LandingOrigin.Other);

        window.ObserveArrival();

        window.IsOpen.Should().BeFalse();
        window.IsActive().Should().BeFalse();
    }

    [Fact]
    public void ObserveProgress_ClosesWithoutProgress()
    {
        // D37-d: 窓の中で出したシークの着地で、不足が減っていなければ閉じる。
        (SeekLandingWindow window, _) = Create();
        window.NotifyLanding(LandingOrigin.Other);
        window.SetSeekDeficit(3.0);
        window.OnSeekSent();
        window.AwaitingObservation.Should().BeTrue("窓の中のシークは着地観測を arm する");

        window.ObserveProgress(ltcSeconds: 10.0, playbackSeconds: 7.0);   // 不足 3.0 のまま

        window.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void ObserveProgress_KeepsWindowWithProgress()
    {
        (SeekLandingWindow window, _) = Create();
        window.NotifyLanding(LandingOrigin.Other);
        window.SetSeekDeficit(3.0);
        window.OnSeekSent();

        window.ObserveProgress(ltcSeconds: 10.0, playbackSeconds: 8.0);   // 不足 3.0 → 2.0

        window.IsOpen.Should().BeTrue();
        window.AwaitingObservation.Should().BeFalse("観測待ちは 1 回で消費する");
    }

    [Fact]
    public void OnSeekSent_OutsideWindow_DoesNotArmObservation()
    {
        (SeekLandingWindow window, _) = Create();

        window.SetSeekDeficit(3.0);   // 窓の外なので覚えない
        window.OnSeekSent();

        window.IsOpen.Should().BeFalse();
        window.AwaitingObservation.Should().BeFalse();
    }
}
