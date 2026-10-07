using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

public class GstBackendStateTests
{
    [Fact]
    public void SenderName_EnvOverrideIsHonored()
    {
        string name = "TCS-Test-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(GstBackendState.SenderNameEnvVar, name);
        try
        {
            GstBackendState.ResolveSenderName().Should().Be(name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GstBackendState.SenderNameEnvVar, null);
        }
    }

    [Theory]
    [InlineData(null, "TimecodeSyncPlayer")]
    [InlineData("", "TimecodeSyncPlayer")]
    [InlineData("   ", "TimecodeSyncPlayer")]
    [InlineData("  Stage A  ", "Stage A")]
    [InlineData("StageB", "StageB")]
    public void SenderName_FromEnvironment_IsTheSameOnBothSides(string? value, string expected)
    {
        // v0.6.6 R-11: shim に渡す名前（GstBackendState）と OutputEngine に渡す名前（MainWindow）は同じ関数で決める。
        // 同じ環境変数を書き換える試験はこのクラスにまとめる（並列で走らせない）。
        string? previous = Environment.GetEnvironmentVariable(GstBackendState.SenderNameEnvVar);
        Environment.SetEnvironmentVariable(GstBackendState.SenderNameEnvVar, value);
        try
        {
            GstBackendState.ResolveSenderName().Should().Be(expected);
            SpoutDefaults.SenderNameFromEnvironment().Should().Be(expected);
            new GstBackendState(new FakeGstNative()).SenderName.Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GstBackendState.SenderNameEnvVar, previous);
        }
    }

    [Fact]
    public void SetExternalDevice_IsAdoptedOnPlayerCreate()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(0x11) };
        var state = new GstBackendState(native);
        var engineDevice = new IntPtr(0x1234_5678);

        state.SetExternalDevice(engineDevice);
        state.EnsurePlayer().Should().BeTrue();

        state.Player.Should().Be(new IntPtr(0x11));
        native.PlayerCreateCalls.Should().Be(1);
        native.LastExternalDevice.Should().Be(engineDevice);
    }

    [Fact]
    public void RecreatePlayer_KeepsTheFrameCallbackRegistered()
    {
        // 0.4.6: Codex のレビューで再現されたもの。GPU 復旧でプレイヤーを作り直すと、
        // 作り直す前に通知の登録情報ごと消していたため、新しいプレイヤーへ登録されなかった
        // （登録状態が True → False）。
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(0x21) };
        var state = new GstBackendState(native);
        state.EnsurePlayer().Should().BeTrue();
        int notified = 0;
        state.AttachRenderCallback(_ => notified++, IntPtr.Zero);
        native.LastFrameCallback.Should().NotBeNull();

        state.RecreatePlayer(new IntPtr(0x99)).Should().BeTrue();

        native.LastFrameCallback.Should().NotBeNull("作り直したプレイヤーにも通知が登録されている");
        native.LastFrameCallback!(IntPtr.Zero, 1, 1);
        notified.Should().Be(1, "登録された通知が、元の受け手へ届く");
    }

    [Fact]
    public void DisposePlayer_IsIdempotentAndBlocksEnsure()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(5) };
        var state = new GstBackendState(native);
        state.EnsurePlayer().Should().BeTrue();

        state.DisposePlayer();
        state.DisposePlayer();

        state.Player.Should().Be(IntPtr.Zero);
        state.EnsurePlayer().Should().BeFalse();
    }

    [Fact]
    public async Task SoftwareDecodeMode_IsForwardedToShimAtPlayerCreate()
    {
        AppSettingsManager manager = await CreateSettingsManager("software");
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        var state = new GstBackendState(native, manager);

        state.EnsurePlayer().Should().BeTrue();

        state.Player.Should().Be(new IntPtr(9));
        native.SetDecodeModeCalls.Should().Equal(GstNative.DecodeModeSoftware);
    }

    [Fact]
    public async Task HardwareDecodeMode_DoesNotTouchShim()
    {
        AppSettingsManager manager = await CreateSettingsManager("hardware");
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        var state = new GstBackendState(native, manager);

        state.EnsurePlayer();

        native.SetDecodeModeCalls.Should().BeEmpty("既定 hardware は shim の既定と同じ");
    }

    [Fact]
    public void MissingSettingsManager_KeepsHardwareDefault()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        var state = new GstBackendState(native);

        state.EnsurePlayer();

        native.SetDecodeModeCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("auto", GstNative.ProResGpuAuto)]
    [InlineData("on", GstNative.ProResGpuOn)]
    [InlineData("off", GstNative.ProResGpuOff)]
    [InlineData("OFF", GstNative.ProResGpuOff)]
    [InlineData("bogus", GstNative.ProResGpuAuto)]
    public async Task ProResGpu_IsForwardedToShimAtPlayerCreate(string value, int expected)
    {
        // v0.6.0: decodeMode と違い、既定の auto でも必ず渡す（shim のログの source=setting で確かめる）。
        AppSettingsManager manager = await CreateSettingsManagerFromJson("{\"proResGpu\":\"" + value + "\"}");
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        var state = new GstBackendState(native, manager);

        state.EnsurePlayer().Should().BeTrue();

        native.SetProResGpuCalls.Should().Equal(expected);
    }

    [Fact]
    public async Task ProResGpu_WithoutKey_ForwardsAuto()
    {
        AppSettingsManager manager = await CreateSettingsManager("hardware");
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        var state = new GstBackendState(native, manager);

        state.EnsurePlayer();

        native.SetProResGpuCalls.Should().Equal(GstNative.ProResGpuAuto);
        state.AppliedProResGpu.Should().Be(ProResGpuMode.Auto);
    }

    [Fact]
    public void ProResGpu_MissingSettingsManager_ForwardsAuto()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        var state = new GstBackendState(native);

        state.AppliedProResGpu.Should().BeNull("プレイヤー作成の前は何も渡していない");
        state.EnsurePlayer();

        native.SetProResGpuCalls.Should().Equal(GstNative.ProResGpuAuto);
    }

    [Fact]
    public async Task ProResGpu_IsForwardedAgainWhenPlayerIsRecreated()
    {
        // GPU 復旧の作り直し（RecreatePlayer）でも、同じ経路（EnsurePlayer）で掛かる。
        AppSettingsManager manager = await CreateSettingsManagerFromJson("{\"proResGpu\":\"off\"}");
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        var state = new GstBackendState(native, manager);
        state.EnsurePlayer().Should().BeTrue();

        state.RecreatePlayer(new IntPtr(0x99)).Should().BeTrue();

        native.PlayerCreateCalls.Should().Be(2);
        native.SetProResGpuCalls.Should().Equal(GstNative.ProResGpuOff, GstNative.ProResGpuOff);
        state.AppliedProResGpu.Should().Be(ProResGpuMode.Off);
    }

    [Fact]
    public async Task ProResGpu_RecreateAfterSettingChange_KeepsTheStartupValue()
    {
        // UI で変えた値は次の起動まで shim に渡さない（「再起動の後に反映」と挙動を合わせる。
        // 本番中の GPU 復旧で復号の方式が黙って変わらないようにする）。
        AppSettingsManager manager = await CreateSettingsManagerFromJson("{\"proResGpu\":\"on\"}");
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9) };
        var state = new GstBackendState(native, manager);
        state.EnsurePlayer().Should().BeTrue();

        await manager.UpdateAsync(settings => settings with { ProResGpu = "off" });
        state.RecreatePlayer(new IntPtr(0x99)).Should().BeTrue();

        manager.Current.ProResGpu.Should().Be("off");
        native.SetProResGpuCalls.Should().Equal(GstNative.ProResGpuOn, GstNative.ProResGpuOn);
        state.AppliedProResGpu.Should().Be(ProResGpuMode.On);
    }

    [Fact]
    public async Task ProResGpu_FailureReturnCode_DoesNotStopPlayerCreate()
    {
        AppSettingsManager manager = await CreateSettingsManagerFromJson("{\"proResGpu\":\"on\"}");
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(9), SetProResGpuResult = -1 };
        var state = new GstBackendState(native, manager);
        state.AttachRenderCallback(_ => { }, IntPtr.Zero);

        state.EnsurePlayer().Should().BeTrue("失敗は警告だけで起動は止めない");

        state.Player.Should().Be(new IntPtr(9));
        native.SetProResGpuCalls.Should().Equal(GstNative.ProResGpuOn);
        native.LastFrameCallback.Should().NotBeNull("失敗の後も通知の登録まで進む");
    }

    private static Task<AppSettingsManager> CreateSettingsManager(string decodeMode) =>
        CreateSettingsManagerFromJson("{\"decodeMode\":\"" + decodeMode + "\"}");

    private static async Task<AppSettingsManager> CreateSettingsManagerFromJson(string json)
    {
        string path = Path.Combine(
            Path.GetTempPath(), "tcs-decode-mode-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, json);
        var manager = new AppSettingsManager(path);
        await manager.LoadAsync();
        return manager;
    }
}
