using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer;

namespace TimecodeSyncPlayer.Tests;

public class ProResGpuPolicyTests
{
    [Theory]
    [InlineData(null, "Auto")]
    [InlineData("", "Auto")]
    [InlineData("   ", "Auto")]
    [InlineData("auto", "Auto")]
    [InlineData("AUTO", "Auto")]
    [InlineData("on", "On")]
    [InlineData("On", "On")]
    [InlineData("off", "Off")]
    [InlineData("OFF", "Off")]
    [InlineData("bogus", "Auto")]
    public void Resolve_MapsValues(string? value, string expected)
    {
        ProResGpuPolicy.Resolve(value).Should().Be(Enum.Parse<ProResGpuMode>(expected));
    }

    [Fact]
    public void Resolve_UnknownValue_WarnsOnce()
    {
        int warnings = 0;

        ProResGpuPolicy.Resolve("enabled", _ => warnings++).Should().Be(ProResGpuMode.Auto);

        warnings.Should().Be(1);
    }

    [Fact]
    public void Resolve_KnownAndEmptyValues_DoNotWarn()
    {
        int warnings = 0;

        ProResGpuPolicy.Resolve("auto", _ => warnings++);
        ProResGpuPolicy.Resolve("on", _ => warnings++);
        ProResGpuPolicy.Resolve("off", _ => warnings++);
        ProResGpuPolicy.Resolve(null, _ => warnings++);

        warnings.Should().Be(0);
    }

    [Theory]
    [InlineData("Auto", "auto")]
    [InlineData("On", "on")]
    [InlineData("Off", "off")]
    public void Describe_RoundTripsThroughResolve(string modeName, string value)
    {
        ProResGpuMode mode = Enum.Parse<ProResGpuMode>(modeName);
        ProResGpuPolicy.Describe(mode).Should().Be(value);
        ProResGpuPolicy.Resolve(ProResGpuPolicy.Describe(mode)).Should().Be(mode);
    }

    [Fact]
    public void RestartNotice_OnlyWhenSelectionDiffersFromStartup()
    {
        ProResGpuPolicy.RestartNotice(ProResGpuMode.Auto, ProResGpuMode.Auto).Should().BeEmpty();
        ProResGpuPolicy.RestartNotice(ProResGpuMode.Auto, ProResGpuMode.Off).Should().Be("再起動の後に反映");
        ProResGpuPolicy.RestartNotice(ProResGpuMode.Off, ProResGpuMode.Off).Should().BeEmpty();
    }

    [Fact]
    public void AppSettings_DefaultsToAuto()
    {
        AppSettings.Default.ProResGpu.Should().Be("auto");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("on")]
    [InlineData("off")]
    public async Task Settings_SaveAndReload_KeepsTheValue(string value)
    {
        string path = TempSettingsPath();
        try
        {
            var writer = new AppSettingsManager(path);
            await writer.UpdateAsync(settings => settings with { ProResGpu = value });

            var reader = new AppSettingsManager(path);
            await reader.LoadAsync();

            reader.Current.ProResGpu.Should().Be(value);
            File.ReadAllText(path).Should().Contain("\"proResGpu\": \"" + value + "\"");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Settings_WithoutKey_LoadsAsAuto()
    {
        // v0.5.x の設定ファイル（proResGpu が無い）はそのまま読めて auto になる（移行なし）。
        string path = TempSettingsPath();
        try
        {
            await File.WriteAllTextAsync(path, "{\"decodeMode\":\"hardware\",\"syncMode\":0}");
            var manager = new AppSettingsManager(path);
            await manager.LoadAsync();

            manager.Current.ProResGpu.Should().Be("auto");
            ProResGpuPolicy.Resolve(manager.Current.ProResGpu).Should().Be(ProResGpuMode.Auto);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "tcs-prores-gpu-" + Guid.NewGuid().ToString("N") + ".json");
}
