using FluentAssertions;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

/// <summary>
/// v0.6.0 段 1: 同梱でないとき（開発機・試験）だけ、exe の隣の gst-extra-plugins を GST_PLUGIN_PATH に足す
/// （既存の値があれば ';' で連結）。同梱のときはプラグインが lib\gstreamer-1.0 に入るので足さない。
/// </summary>
public class GstExtraPluginPathTests
{
    private const string ExtraDir = @"C:\app\gst-extra-plugins";

    [Fact]
    public void EnvironmentVariableRoot_FolderExists_AddsFolder()
    {
        GstNativeLibraryResolver.ComposeExtraPluginPath(GstRootSource.EnvironmentVariable, null, ExtraDir, _ => true)
            .Should().Be(ExtraDir);
    }

    [Fact]
    public void ProgramFilesRoot_FolderExists_AddsFolder()
    {
        GstNativeLibraryResolver.ComposeExtraPluginPath(GstRootSource.ProgramFiles, "", ExtraDir, _ => true)
            .Should().Be(ExtraDir, "空の値は既存の値として扱わない");
    }

    [Fact]
    public void NotBundled_FolderExists_AppendsToExistingValue()
    {
        GstNativeLibraryResolver.ComposeExtraPluginPath(
                GstRootSource.ProgramFiles, @"C:\other\plugins", ExtraDir, _ => true)
            .Should().Be(@"C:\other\plugins;" + ExtraDir, "既存の値は残して ';' で連結する");
    }

    [Fact]
    public void NotBundled_FolderAlreadyListed_DoesNotDuplicate()
    {
        GstNativeLibraryResolver.ComposeExtraPluginPath(
                GstRootSource.ProgramFiles, @"C:\other\plugins;" + ExtraDir, ExtraDir, _ => true)
            .Should().BeNull();
    }

    [Fact]
    public void NotBundled_FolderMissing_DoesNotChange()
    {
        GstNativeLibraryResolver.ComposeExtraPluginPath(GstRootSource.ProgramFiles, null, ExtraDir, _ => false)
            .Should().BeNull();
    }

    [Fact]
    public void Bundled_FolderExists_DoesNotAdd()
    {
        GstNativeLibraryResolver.ComposeExtraPluginPath(GstRootSource.Bundled, null, ExtraDir, _ => true)
            .Should().BeNull("同梱のときはプラグインが lib\\gstreamer-1.0 に入る");
    }
}
