using System.IO;
using System.Text.Json;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Default_ReturnsExpectedDefaults()
    {
        var settings = AppSettings.Default;

        settings.SyncMode.Should().Be(SyncMode.Single);
        settings.GapBehavior.Should().Be(GapBehavior.Freeze);
        settings.TimecodeFpsMode.Should().Be(TimecodeFpsMode.Auto);
        settings.LastOpenedProjectPath.Should().BeEmpty();
        settings.LtcDeviceIndex.Should().Be(-1);
        settings.WindowLeft.Should().BeNull();
        settings.WindowTop.Should().BeNull();
        settings.WindowWidth.Should().BeNull();
        settings.WindowHeight.Should().BeNull();
        settings.IsTimelineVisible.Should().BeFalse();
        settings.AutoOffsetOnAdd.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7681)]
    public void ValidateSettings_RejectsInvalidWindowWidth(int invalidWidth)
    {
        var settings = AppSettings.Default with { WindowWidth = invalidWidth };

        var result = AppSettingsManager.ValidateSettings(settings);

        result.WindowWidth.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4321)]
    public void ValidateSettings_RejectsInvalidWindowHeight(int invalidHeight)
    {
        var settings = AppSettings.Default with { WindowHeight = invalidHeight };

        var result = AppSettingsManager.ValidateSettings(settings);

        result.WindowHeight.Should().BeNull();
    }

    [Theory]
    [InlineData(-7681)]
    [InlineData(7681)]
    public void ValidateSettings_RejectsInvalidWindowLeft(double invalidLeft)
    {
        var settings = AppSettings.Default with { WindowLeft = invalidLeft };

        var result = AppSettingsManager.ValidateSettings(settings);

        result.WindowLeft.Should().BeNull();
    }

    [Theory]
    [InlineData(-8000)]
    [InlineData(5000)]
    public void ValidateSettings_RejectsInvalidWindowTop(double invalidTop)
    {
        var settings = AppSettings.Default with { WindowTop = invalidTop };

        var result = AppSettingsManager.ValidateSettings(settings);

        result.WindowTop.Should().BeNull();
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-10)]
    public void ValidateSettings_RejectsInvalidLtcDeviceIndex(int invalidIndex)
    {
        var settings = AppSettings.Default with { LtcDeviceIndex = invalidIndex };

        var result = AppSettingsManager.ValidateSettings(settings);

        result.LtcDeviceIndex.Should().Be(-1);
    }

    [Fact]
    public void ValidateSettings_PassesValidSettingsThrough()
    {
        var settings = AppSettings.Default with
        {
            WindowWidth = 1920,
            WindowHeight = 1080,
            WindowLeft = 100,
            WindowTop = 50,
            LtcDeviceIndex = 2
        };

        var result = AppSettingsManager.ValidateSettings(settings);

        result.WindowWidth.Should().Be(1920);
        result.WindowHeight.Should().Be(1080);
        result.WindowLeft.Should().Be(100);
        result.WindowTop.Should().Be(50);
        result.LtcDeviceIndex.Should().Be(2);
    }

    [Fact]
    public void WithExpression_CloneDoesNotModifyOriginal()
    {
        var original = AppSettings.Default;
        var clone = original with { WindowWidth = 800 };

        original.WindowWidth.Should().BeNull();
        clone.WindowWidth.Should().Be(800);
    }

    [Fact]
    public void SerializationRoundtrip_PreservesValues()
    {
        var original = AppSettings.Default with
        {
            SyncMode = SyncMode.Continue,
            GapBehavior = GapBehavior.Black,
            TimecodeFpsMode = TimecodeFpsMode.Fixed24,
            LastOpenedProjectPath = @"C:\projects\test.json",
            LtcDeviceIndex = 3,
            WindowLeft = 200,
            WindowTop = 100,
            WindowWidth = 1280,
            WindowHeight = 720,
            IsTimelineVisible = true,
            AutoOffsetOnAdd = false
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        string json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json, options);

        deserialized.Should().NotBeNull();
        deserialized!.SyncMode.Should().Be(original.SyncMode);
        deserialized.GapBehavior.Should().Be(original.GapBehavior);
        deserialized.TimecodeFpsMode.Should().Be(original.TimecodeFpsMode);
        deserialized.LastOpenedProjectPath.Should().Be(original.LastOpenedProjectPath);
        deserialized.LtcDeviceIndex.Should().Be(original.LtcDeviceIndex);
        deserialized.WindowLeft.Should().Be(original.WindowLeft);
        deserialized.WindowTop.Should().Be(original.WindowTop);
        deserialized.WindowWidth.Should().Be(original.WindowWidth);
        deserialized.WindowHeight.Should().Be(original.WindowHeight);
        deserialized.IsTimelineVisible.Should().Be(original.IsTimelineVisible);
        deserialized.AutoOffsetOnAdd.Should().Be(original.AutoOffsetOnAdd);
    }

    [Fact]
    public void Instance_ReturnsSameSingleton()
    {
        AppSettingsManager.ResetForTesting();

        var instance1 = AppSettingsManager.Instance;
        var instance2 = AppSettingsManager.Instance;

        instance1.Should().BeSameAs(instance2);
    }

    [Fact]
    public void ResetForTesting_CreatesNewInstance()
    {
        var instance1 = AppSettingsManager.Instance;
        AppSettingsManager.ResetForTesting();
        var instance2 = AppSettingsManager.Instance;

        instance1.Should().NotBeSameAs(instance2);
    }
}
