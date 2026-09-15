using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>T5: 同期補正モードの設定と検証（UI 配線の土台）。</summary>
public class SyncCorrectionSettingsTests
{
    [Fact]
    public void AppSettings_DefaultsToSmooth()
    {
        AppSettings.Default.SyncCorrectionMode.Should().Be(SyncCorrectionMode.Smooth);
    }

    [Fact]
    public void ValidateSettings_KeepsValidCorrectionMode()
    {
        AppSettings settings = AppSettings.Default with { SyncCorrectionMode = SyncCorrectionMode.Jump };

        AppSettingsManager.ValidateSettings(settings).SyncCorrectionMode.Should().Be(SyncCorrectionMode.Jump);
    }

    [Fact]
    public void ValidateSettings_InvalidCorrectionMode_FallsBackToSmooth()
    {
        AppSettings settings = AppSettings.Default with { SyncCorrectionMode = (SyncCorrectionMode)99 };

        AppSettingsManager.ValidateSettings(settings).SyncCorrectionMode.Should().Be(SyncCorrectionMode.Smooth);
    }
}
