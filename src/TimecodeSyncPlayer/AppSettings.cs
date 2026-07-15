using System.IO;
using System.Text.Json;
using System.Threading;

namespace TimecodeSyncPlayer;

public enum LtcSignalLossMode
{
    RunThrough,
    Stop
}

/// <summary>
/// アプリケーション設定の不変レコード。
/// </summary>
public sealed record AppSettings
{
    public const int DefaultLtcSignalLossTimeoutMs = 250;
    public const int MinimumLtcSignalLossTimeoutMs = 100;
    public const int MaximumLtcSignalLossTimeoutMs = 5000;
    public const int DefaultLtcSignalResumeFrames = 5;

    public SyncMode SyncMode { get; init; } = SyncMode.Single;
    public GapBehavior GapBehavior { get; init; } = GapBehavior.Freeze;
    public TimecodeFpsMode TimecodeFpsMode { get; init; } = TimecodeFpsMode.Auto;
    public string LastOpenedProjectPath { get; init; } = "";
    public int LtcDeviceIndex { get; init; } = -1;
    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    public bool IsTimelineVisible { get; init; }
    public bool AutoOffsetOnAdd { get; init; } = true;
    public LtcSignalLossMode LtcSignalLossMode { get; init; } = LtcSignalLossMode.RunThrough;
    public int LtcSignalLossTimeoutMs { get; init; } = DefaultLtcSignalLossTimeoutMs;
    public int LtcSignalResumeFrames { get; init; } = DefaultLtcSignalResumeFrames;

    public static AppSettings Default => new();
}

/// <summary>
/// アプリケーション設定の永続化を担当する。
/// </summary>
public sealed class AppSettingsManager
{
    public const string SettingsPathEnvironmentVariable = "TIMECODE_SYNC_PLAYER_SETTINGS_PATH";

    private static readonly string DefaultSettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TimecodeSyncPlayer");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static AppSettingsManager? _instance;
    private static readonly object _lock = new();

    private readonly string _settingsFilePath;
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);

    public AppSettings Current { get; private set; } = AppSettings.Default;

    public static AppSettingsManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new AppSettingsManager(ResolveSettingsFilePath());
                }
            }
            return _instance;
        }
    }

    public static void ResetForTesting()
    {
        lock (_lock)
        {
            _instance = null;
        }
    }

    internal AppSettingsManager(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
    }

    internal static string ResolveSettingsFilePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable);
        return ResolveSettingsFilePath(overridePath);
    }

    internal static string ResolveSettingsFilePath(string? overridePath)
    {
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(DefaultSettingsDirectory, "settings.json")
            : Path.GetFullPath(overridePath);
    }

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return;
            }

            string json = await File.ReadAllTextAsync(_settingsFilePath, System.Text.Encoding.UTF8)
                .ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded != null)
            {
                Current = ValidateSettings(loaded);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to load settings, using defaults");
            Current = AppSettings.Default;
        }
    }

    internal static AppSettings ValidateSettings(AppSettings settings)
    {
        if (settings.WindowWidth is <= 0 or > 7680) settings = settings with { WindowWidth = null };
        if (settings.WindowHeight is <= 0 or > 4320) settings = settings with { WindowHeight = null };
        if (settings.WindowLeft is < -7680 or > 7680) settings = settings with { WindowLeft = null };
        if (settings.WindowTop is < -4320 or > 4320) settings = settings with { WindowTop = null };
        if (settings.LtcDeviceIndex < -1) settings = settings with { LtcDeviceIndex = -1 };
        if (!Enum.IsDefined(settings.LtcSignalLossMode))
            settings = settings with { LtcSignalLossMode = LtcSignalLossMode.RunThrough };
        settings = settings with
        {
            LtcSignalLossTimeoutMs = Math.Clamp(
                settings.LtcSignalLossTimeoutMs,
                AppSettings.MinimumLtcSignalLossTimeoutMs,
                AppSettings.MaximumLtcSignalLossTimeoutMs),
            LtcSignalResumeFrames = settings.LtcSignalResumeFrames > 0
                ? settings.LtcSignalResumeFrames
                : AppSettings.DefaultLtcSignalResumeFrames,
        };

        return settings;
    }

    public async Task SaveAsync()
    {
        await SaveAsync(Current);
    }

    public async Task SaveAsync(AppSettings settings)
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(_settingsFilePath))!;
            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(_settingsFilePath, json, System.Text.Encoding.UTF8);
            Current = settings;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to save settings");
        }
    }

    public void Update(Func<AppSettings, AppSettings> modifier)
    {
        _ = UpdateAsync(modifier);
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> modifier)
    {
        await _updateSemaphore.WaitAsync();
        try
        {
            Current = modifier(Current);
            await SaveAsync(Current);
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }
}
