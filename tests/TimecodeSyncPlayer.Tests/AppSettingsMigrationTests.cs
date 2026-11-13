using System.IO;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.7: 設定の保存先を %LOCALAPPDATA%\ERYTHEIA へ移すときの引き継ぎ（旧の %LOCALAPPDATA%\TimecodeSyncPlayer から写す）。
/// Log.Logger を差し替えるため ProjectSerializerCanvasTests と同じ collection で直列実行する。
/// </summary>
[Collection("Project serializer state")]
public class AppSettingsMigrationTests
{
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    [Fact]
    public async Task LoadAsync_OnlyLegacySettings_CopiesThemAndKeepsTheLegacyFile()
    {
        using var dirs = new MigrationDirectories();
        Directory.CreateDirectory(dirs.LegacyDirectory);
        await File.WriteAllTextAsync(dirs.LegacyPath, """{"syncOffsetMs":12.5,"ltcDeviceName":"CABLE Output","volume":55}""");

        (AppSettingsManager manager, LogEvent[] events) = await LoadWithLogAsync(dirs);

        manager.Current.SyncOffsetMs.Should().Be(12.5);
        manager.Current.LtcDeviceName.Should().Be("CABLE Output");
        manager.Current.Volume.Should().Be(55);
        File.Exists(dirs.NewPath).Should().BeTrue("新しい場所へ写す");
        (await File.ReadAllTextAsync(dirs.NewPath)).Should().Be(await File.ReadAllTextAsync(dirs.LegacyPath));
        File.Exists(dirs.LegacyPath).Should().BeTrue("旧の場所は消さない");
        File.Exists(dirs.NewPath + ".migrating").Should().BeFalse();
        events.Where(IsCopiedInformation).Should().HaveCount(1);
        events.Where(IsFailedWarning).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_NewSettingsExist_DoesNotCopy()
    {
        using var dirs = new MigrationDirectories();
        Directory.CreateDirectory(dirs.LegacyDirectory);
        Directory.CreateDirectory(dirs.NewDirectory);
        await File.WriteAllTextAsync(dirs.LegacyPath, """{"volume":55}""");
        await File.WriteAllTextAsync(dirs.NewPath, """{"volume":42}""");

        (AppSettingsManager manager, LogEvent[] events) = await LoadWithLogAsync(dirs);

        manager.Current.Volume.Should().Be(42, "新しい場所の設定を使う");
        (await File.ReadAllTextAsync(dirs.NewPath)).Should().Be("""{"volume":42}""", "新しい場所の設定を上書きしない");
        (await File.ReadAllTextAsync(dirs.LegacyPath)).Should().Be("""{"volume":55}""");
        events.Where(IsCopiedInformation).Should().BeEmpty();
        events.Where(IsFailedWarning).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_NoLegacySettings_StartsWithDefaults()
    {
        using var dirs = new MigrationDirectories();

        (AppSettingsManager manager, LogEvent[] events) = await LoadWithLogAsync(dirs);

        manager.Current.Should().Be(AppSettings.Default);
        File.Exists(dirs.NewPath).Should().BeFalse();
        Directory.Exists(dirs.NewDirectory).Should().BeFalse("写すものが無いときはフォルダも作らない");
        events.Where(IsCopiedInformation).Should().BeEmpty();
        events.Where(IsFailedWarning).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_CopyFails_StartsWithDefaultsAndWarnsOnce()
    {
        using var dirs = new MigrationDirectories();
        Directory.CreateDirectory(dirs.LegacyDirectory);
        await File.WriteAllTextAsync(dirs.LegacyPath, """{"volume":55}""");

        AppSettingsManager manager;
        LogEvent[] events;
        // 旧の設定を共有なしで開いたままにして、写すのを失敗させる。
        using (new FileStream(dirs.LegacyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            (manager, events) = await LoadWithLogAsync(dirs);
        }

        manager.Current.Should().Be(AppSettings.Default);
        File.Exists(dirs.NewPath).Should().BeFalse("写すのに失敗したら新しい場所に何も残さない");
        File.Exists(dirs.NewPath + ".migrating").Should().BeFalse();
        File.Exists(dirs.LegacyPath).Should().BeTrue();
        events.Where(IsFailedWarning).Should().HaveCount(1);
        events.Where(IsCopiedInformation).Should().BeEmpty();
    }

    private static async Task<(AppSettingsManager Manager, LogEvent[] Events)> LoadWithLogAsync(MigrationDirectories dirs)
    {
        var sink = new ListSink();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var manager = new AppSettingsManager(dirs.NewPath, legacySettingsFilePath: dirs.LegacyPath);
        try
        {
            await manager.LoadAsync();
        }
        finally
        {
            Log.Logger = previous;
        }

        LogEvent[] events;
        lock (sink.Events) events = sink.Events.ToArray();
        // Log.Logger はプロセス全体で共有されるため、この試験の経路の行だけに絞る。
        return (manager, events.Where(e => e.RenderMessage().Contains(dirs.Root, StringComparison.Ordinal)).ToArray());
    }

    private static bool IsCopiedInformation(LogEvent logEvent) =>
        logEvent.Level == LogEventLevel.Information
        && logEvent.RenderMessage().Contains("旧の場所の設定を新しい場所へ写しました", StringComparison.Ordinal);

    private static bool IsFailedWarning(LogEvent logEvent) =>
        logEvent.Level == LogEventLevel.Warning
        && logEvent.RenderMessage().Contains("旧の場所の設定を写せませんでした", StringComparison.Ordinal);

    private sealed class MigrationDirectories : IDisposable
    {
        public MigrationDirectories()
        {
            Root = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", "migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string LegacyDirectory => Path.Combine(Root, "TimecodeSyncPlayer");
        public string NewDirectory => Path.Combine(Root, "ERYTHEIA");
        public string LegacyPath => Path.Combine(LegacyDirectory, "settings.json");
        public string NewPath => Path.Combine(NewDirectory, "settings.json");

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* 後始末は失敗しても続ける */ }
        }
    }
}
