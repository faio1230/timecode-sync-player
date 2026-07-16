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
        settings.LtcSignalLossMode.Should().Be(LtcSignalLossMode.RunThrough);
        settings.LtcSignalLossTimeoutMs.Should().Be(250);
        settings.LtcSignalResumeFrames.Should().Be(5);
        settings.ShowDebugOsd.Should().BeFalse();
        settings.FullscreenDisplayDeviceName.Should().BeEmpty();
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

    [Theory]
    [InlineData(99, 100)]
    [InlineData(100, 100)]
    [InlineData(250, 250)]
    [InlineData(5000, 5000)]
    [InlineData(5001, 5000)]
    public void ValidateSettings_ClampsLtcSignalLossTimeout(int timeoutMs, int expected)
    {
        AppSettings settings = AppSettings.Default with { LtcSignalLossTimeoutMs = timeoutMs };

        AppSettingsManager.ValidateSettings(settings).LtcSignalLossTimeoutMs.Should().Be(expected);
    }

    [Fact]
    public void ValidateSettings_RejectsInvalidSignalLossModeAndResumeFrameCount()
    {
        AppSettings settings = AppSettings.Default with
        {
            LtcSignalLossMode = (LtcSignalLossMode)99,
            LtcSignalResumeFrames = 0,
        };

        AppSettings result = AppSettingsManager.ValidateSettings(settings);

        result.LtcSignalLossMode.Should().Be(LtcSignalLossMode.RunThrough);
        result.LtcSignalResumeFrames.Should().Be(AppSettings.DefaultLtcSignalResumeFrames);
    }

    [Fact]
    public void ResolveSettingsFilePath_WithRelativeOverride_ReturnsAbsolutePath()
    {
        string relativePath = Path.Combine("test-settings", "settings.json");

        string result = AppSettingsManager.ResolveSettingsFilePath(relativePath);

        result.Should().Be(Path.GetFullPath(relativePath));
        Path.IsPathFullyQualified(result).Should().BeTrue();
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
            AutoOffsetOnAdd = false,
            LtcSignalLossMode = LtcSignalLossMode.Stop,
            LtcSignalLossTimeoutMs = 1200,
            LtcSignalResumeFrames = 8,
            ShowDebugOsd = true,
            FullscreenDisplayDeviceName = @"\\.\DISPLAY2"
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
        deserialized.LtcSignalLossMode.Should().Be(original.LtcSignalLossMode);
        deserialized.LtcSignalLossTimeoutMs.Should().Be(original.LtcSignalLossTimeoutMs);
        deserialized.LtcSignalResumeFrames.Should().Be(original.LtcSignalResumeFrames);
        deserialized.ShowDebugOsd.Should().BeTrue();
        deserialized.FullscreenDisplayDeviceName.Should().Be(original.FullscreenDisplayDeviceName);
    }

    [Fact]
    public void Deserialization_WithoutSignalLossKeysUsesBackwardCompatibleDefaults()
    {
        const string json = """{"syncMode":0}""";
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        AppSettings? deserialized = JsonSerializer.Deserialize<AppSettings>(json, options);

        deserialized.Should().NotBeNull();
        deserialized!.LtcSignalLossMode.Should().Be(LtcSignalLossMode.RunThrough);
        deserialized.LtcSignalLossTimeoutMs.Should().Be(250);
        deserialized.LtcSignalResumeFrames.Should().Be(5);
        deserialized.ShowDebugOsd.Should().BeFalse();
        deserialized.FullscreenDisplayDeviceName.Should().BeEmpty();
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

    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsSettingsInTemporaryDirectory()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var writer = CreateManager(path);
            var expected = AppSettings.Default with
            {
                SyncMode = SyncMode.Continue,
                WindowWidth = 1280,
                LastOpenedProjectPath = @"C:\projects\show.json",
                LtcSignalLossMode = LtcSignalLossMode.Stop,
                LtcSignalLossTimeoutMs = 900,
                LtcSignalResumeFrames = 7,
            };

            await writer.SaveAsync(expected);
            var reader = CreateManager(path);
            await reader.LoadAsync();

            reader.Current.Should().Be(expected);
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_InvalidJsonRestoresDefaults()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ invalid json");
            var manager = CreateManager(path);
            await manager.SaveAsync(AppSettings.Default with { WindowWidth = 800 });
            await File.WriteAllTextAsync(path, "{ invalid json");

            await manager.LoadAsync();

            manager.Current.Should().Be(AppSettings.Default);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_NewDestinationWritesTemporaryFileThenMovesIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var operations = new RecordingAtomicFileOperations();
        var manager = new AppSettingsManager(path, operations);
        AppSettings expected = AppSettings.Default with { WindowWidth = 1280 };

        await manager.SaveAsync(expected);

        operations.Writes.Should().ContainSingle();
        string temporaryPath = operations.Writes.Single().Path;
        temporaryPath.Should().NotBe(path);
        Path.GetDirectoryName(temporaryPath).Should().Be(Path.GetDirectoryName(path));
        operations.Moves.Should().Equal((temporaryPath, path));
        operations.Replaces.Should().BeEmpty();
        manager.Current.Should().Be(expected);
    }

    [Fact]
    public async Task SaveAsync_TemporaryWriteFailurePreservesExistingDestinationAndCurrentSettings()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        const string original = "existing-settings";
        await File.WriteAllTextAsync(path, original);
        try
        {
            var operations = new RecordingAtomicFileOperations(path) { ThrowAfterWrite = true };
            var manager = new AppSettingsManager(path, operations);

            await manager.SaveAsync(AppSettings.Default with { WindowWidth = 1280 });

            (await File.ReadAllTextAsync(path)).Should().Be(original);
            manager.Current.Should().Be(AppSettings.Default);
            Directory.GetFiles(directory, ".*.tmp").Should().BeEmpty();
            operations.Deletes.Should().ContainSingle().Which.Should().Be(operations.Writes.Single().Path);
            operations.Replaces.Should().BeEmpty();
            operations.Moves.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WhenDestinationIsLockedPreservesExistingFile()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        const string original = "locked-existing-settings";
        await File.WriteAllTextAsync(path, original);
        try
        {
            var manager = CreateManager(path);
            await using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await manager.SaveAsync(AppSettings.Default with { WindowWidth = 1280 });
            }

            (await File.ReadAllTextAsync(path)).Should().Be(original);
            manager.Current.Should().Be(AppSettings.Default);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"windowWidth\":800")]
    [InlineData("not-json")]
    [InlineData("")]
    public async Task LoadAsync_CorruptVariantsRestoreDefaultsAndUpdateRegeneratesFile(string content)
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
            var manager = CreateManager(path);

            await manager.LoadAsync();
            manager.Current.Should().Be(AppSettings.Default);

            await manager.UpdateAsync(settings => settings with { WindowWidth = 1280 });

            var reloaded = CreateManager(path);
            await reloaded.LoadAsync();
            reloaded.Current.WindowWidth.Should().Be(1280);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAsync_BomOnlyOrInvalidUtf8BytesRestoresDefaults(bool bomOnly)
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            byte[] bytes = bomOnly
                ? System.Text.Encoding.UTF8.GetPreamble()
                : [0xff, 0xfe, 0xfd];
            await File.WriteAllBytesAsync(path, bytes);
            var manager = CreateManager(path);

            await manager.LoadAsync();

            manager.Current.Should().Be(AppSettings.Default);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MissingAndUnknownKeysUsesDefaultsAndKnownValues()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """{"windowWidth":1280,"unknownFutureKey":{"nested":true}}""",
                System.Text.Encoding.UTF8);
            var manager = CreateManager(path);

            await manager.LoadAsync();

            manager.Current.WindowWidth.Should().Be(1280);
            manager.Current.SyncMode.Should().Be(SyncMode.Single);
            manager.Current.AutoOffsetOnAdd.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_TypeMismatchRestoresDefaults()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"windowWidth":"wide"}""", System.Text.Encoding.UTF8);
            var manager = CreateManager(path);

            await manager.LoadAsync();

            manager.Current.Should().Be(AppSettings.Default);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_MissingFileKeepsDefaults()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var manager = CreateManager(Path.Combine(directory, "missing.json"));

            await manager.LoadAsync();

            manager.Current.Should().Be(AppSettings.Default);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAsync_AppliesConcurrentUpdatesWithoutLosingChanges()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var manager = CreateManager(path);

            await Task.WhenAll(
                manager.UpdateAsync(settings => settings with { WindowWidth = 1024 }),
                manager.UpdateAsync(settings => settings with { WindowHeight = 768 }));

            manager.Current.WindowWidth.Should().Be(1024);
            manager.Current.WindowHeight.Should().Be(768);

            var reloaded = CreateManager(path);
            await reloaded.LoadAsync();
            reloaded.Current.WindowWidth.Should().Be(1024);
            reloaded.Current.WindowHeight.Should().Be(768);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AppSettingsManager CreateManager(string path) => new(path);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"TimecodeSyncPlayer.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
