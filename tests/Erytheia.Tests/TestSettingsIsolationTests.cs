using System.IO;
using Erytheia.Tests.Helpers;
using FluentAssertions;

namespace Erytheia.Tests;

/// <summary>
/// v0.6.7: 試験のプロセスが、利用者の本物の設定（%LOCALAPPDATA%\ERYTHEIA と %LOCALAPPDATA%\TimecodeSyncPlayer）を
/// 書きも読みもしないこと（<see cref="TestSettingsIsolation"/>）。
/// </summary>
public class TestSettingsIsolationTests
{
    [Fact]
    public void Instance_UsesATemporarySettingsFile_AndNeverTheRealOnes()
    {
        AppSettingsManager manager = AppSettingsManager.Instance;

        TestSettingsIsolation.IsUnderRealSettingsDirectory(manager.FilePath).Should().BeFalse(
            $"試験は本物の新旧の設定の場所を書かない（{manager.FilePath}）");
        manager.FilePath.Should().Be(Path.GetFullPath(
            Environment.GetEnvironmentVariable(AppSettingsManager.SettingsPathEnvironmentVariable)!));
        manager.LegacyFilePath.Should().BeNull("上書きのパスがあるときは旧の場所から写さない（旧の settings.json を読まない）");
    }

    [Fact]
    public void RealSettingsDirectories_AreTheNewAndTheOldFolders()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        TestSettingsIsolation.IsUnderRealSettingsDirectory(Path.Combine(localAppData, "ERYTHEIA", "settings.json")).Should().BeTrue();
        TestSettingsIsolation.IsUnderRealSettingsDirectory(Path.Combine(localAppData, "TimecodeSyncPlayer", "settings.json")).Should().BeTrue();
        TestSettingsIsolation.IsUnderRealSettingsDirectory(Path.Combine(localAppData, "erytheia", "settings.json")).Should().BeTrue(
            "Windows はフォルダの名前の大文字小文字を区別しない");
        TestSettingsIsolation.IsUnderRealSettingsDirectory(Path.Combine(Path.GetTempPath(), "Erytheia.Tests", "settings.json")).Should().BeFalse();
    }
}
