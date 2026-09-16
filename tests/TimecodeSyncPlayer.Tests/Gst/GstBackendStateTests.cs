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
