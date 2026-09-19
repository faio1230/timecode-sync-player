using System.IO;
using FluentAssertions;
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

    private static async Task<AppSettingsManager> CreateSettingsManager(string decodeMode)
    {
        string path = Path.Combine(
            Path.GetTempPath(), "tcs-decode-mode-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"decodeMode\":\"" + decodeMode + "\"}");
        var manager = new AppSettingsManager(path);
        await manager.LoadAsync();
        return manager;
    }
}
