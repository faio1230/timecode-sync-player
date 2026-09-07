using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal sealed record MonkeyTestConfiguration(
    bool IsEnabled,
    int Seed,
    int ActionCount,
    string ReportDirectory)
{
    public const string EnabledVariable = "TIMECODE_MONKEY_ENABLED";
    public const string SeedVariable = "TIMECODE_MONKEY_SEED";
    public const string ActionCountVariable = "TIMECODE_MONKEY_ACTIONS";
    public const string ReportDirectoryVariable = "TIMECODE_MONKEY_REPORT_DIR";
    public const int DefaultSeed = 20260907;
    public const int DefaultActionCount = 100;

    public static MonkeyTestConfiguration FromCurrentEnvironment()
    {
        var environment = new Dictionary<string, string?>
        {
            [EnabledVariable] = Environment.GetEnvironmentVariable(EnabledVariable),
            [SeedVariable] = Environment.GetEnvironmentVariable(SeedVariable),
            [ActionCountVariable] = Environment.GetEnvironmentVariable(ActionCountVariable),
            [ReportDirectoryVariable] = Environment.GetEnvironmentVariable(ReportDirectoryVariable),
        };
        return Parse(environment);
    }

    public static MonkeyTestConfiguration Parse(IReadOnlyDictionary<string, string?> environment)
    {
        if (Get(environment, EnabledVariable) != "1")
            return new MonkeyTestConfiguration(false, DefaultSeed, DefaultActionCount, string.Empty);

        int seed = ParseInt(environment, SeedVariable, DefaultSeed, int.MinValue, int.MaxValue, "int");
        int actionCount = ParseInt(environment, ActionCountVariable, DefaultActionCount, 1, 100_000, "1..100000");
        string reportDirectory = Get(environment, ReportDirectoryVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reportDirectory) || !Path.IsPathFullyQualified(reportDirectory))
        {
            throw new InvalidOperationException(
                $"{ReportDirectoryVariable} must be an absolute path when monkey testing is enabled.");
        }

        return new MonkeyTestConfiguration(
            true,
            seed,
            actionCount,
            Path.GetFullPath(reportDirectory));
    }

    private static int ParseInt(
        IReadOnlyDictionary<string, string?> environment,
        string name,
        int defaultValue,
        int minimum,
        int maximum,
        string contract)
    {
        string? text = Get(environment, name);
        if (string.IsNullOrWhiteSpace(text))
            return defaultValue;

        if (!int.TryParse(text, out int parsed) || parsed < minimum || parsed > maximum)
            throw new InvalidOperationException($"{name} must be {contract}.");

        return parsed;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out string? value) ? value : null;
}

internal enum MonkeyOperationKind
{
    TogglePlayback,
    SeekBurst,
    PreviousTrack,
    NextTrack,
    ToggleSync,
    ToggleMonitor,
    SelectSyncMode,
    SelectGapBehavior,
    SelectSignalLossMode,
    StopSignal,
    RestartSignal,
    JumpSignal,
    NoisySignal,
}

internal sealed record MonkeyOperation(
    int Index,
    MonkeyOperationKind Kind,
    IReadOnlyList<double> SeekRatios,
    int SyncModeIndex,
    int GapBehaviorIndex,
    int SignalLossModeIndex,
    int SignalStartSeconds,
    int NoiseSeed,
    double NoiseAmplitude);

internal static class MonkeyOperationGenerator
{
    private const int KindCount = 13;

    public static IReadOnlyList<MonkeyOperation> Generate(int seed, int count)
    {
        if (count is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(count));

        var random = new StableRandom(unchecked((uint)seed));
        var operations = new MonkeyOperation[count];
        for (int index = 0; index < operations.Length; index++)
        {
            operations[index] = new MonkeyOperation(
                Index: index,
                Kind: (MonkeyOperationKind)(random.NextUInt32() % KindCount),
                SeekRatios:
                [
                    (random.NextUInt32() % 1001) / 1000.0,
                    (random.NextUInt32() % 1001) / 1000.0,
                    (random.NextUInt32() % 1001) / 1000.0,
                ],
                SyncModeIndex: (int)(random.NextUInt32() % 2),
                GapBehaviorIndex: (int)(random.NextUInt32() % 2),
                SignalLossModeIndex: (int)(random.NextUInt32() % 2),
                SignalStartSeconds: (int)(random.NextUInt32() % 60),
                NoiseSeed: unchecked((int)random.NextUInt32()),
                NoiseAmplitude: (20 + random.NextUInt32() % 131) / 1000.0);
        }

        return operations;
    }

    private sealed class StableRandom
    {
        private uint _state;

        public StableRandom(uint seed) => _state = seed;

        public uint NextUInt32()
        {
            _state = unchecked(_state * 1_664_525U + 1_013_904_223U);
            // Mix the output before bounded draws. Raw LCG low bits would make every
            // mode constant when a fixed even number of draws is consumed per action.
            uint value = _state;
            value = unchecked((value ^ (value >> 16)) * 0x7feb352dU);
            value = unchecked((value ^ (value >> 15)) * 0x846ca68bU);
            return value ^ (value >> 16);
        }
    }
}

internal sealed class MonkeyRunSummary
{
    public bool Success { get; set; }
    public int Seed { get; set; }
    public int RequestedActions { get; set; }
    public int CompletedActions { get; set; }
    public int ExecutedActions { get; set; }
    public int AttemptedActions { get; set; }
    public int UnavailableActions { get; set; }
    public int SampleCount { get; set; }
    public long PeakPrivateMemoryBytes { get; set; }
    public int PeakHandleCount { get; set; }
    public int? ProcessId { get; set; }
    public int? ExitCode { get; set; }
    public string? StartedUtc { get; set; }
    public string? EndedUtc { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public int? FailedActionIndex { get; set; }
    public string? FailureType { get; set; }
    public string? FailureMessage { get; set; }
    public string? InitialLtc { get; set; }
    public string? FinalLtc { get; set; }
    public string? InitialPlayback { get; set; }
    public string? FinalPlayback { get; set; }
}

internal static class MonkeyJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string SerializeSummary(MonkeyRunSummary summary) =>
        JsonSerializer.Serialize(summary, Options);

    public static void WriteSummary(string path, MonkeyRunSummary summary)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, SerializeSummary(summary), new UTF8Encoding(false));
    }

    public static void WriteAppProcessMarker(string path, Process process)
    {
        process.Refresh();
        var marker = new
        {
            processId = process.Id,
            startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(marker, Options), new UTF8Encoding(false));
    }
}

internal sealed class MonkeyJournal : IDisposable
{
    private readonly int _seed;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly StreamWriter _writer;

    public MonkeyJournal(string path, int seed)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _seed = seed;
    }

    public long ElapsedMilliseconds => _stopwatch.ElapsedMilliseconds;

    public void Write(
        string eventName,
        int? actionIndex = null,
        MonkeyOperationKind? operation = null,
        object? details = null,
        Process? process = null)
    {
        object? processState = CaptureProcessState(process);
        var entry = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            elapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
            @event = eventName,
            seed = _seed,
            actionIndex,
            operation = operation?.ToString(),
            process = processState,
            details,
        };

        _writer.WriteLine(JsonSerializer.Serialize(entry, MonkeyJson.Options));
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();

    private static object? CaptureProcessState(Process? process)
    {
        if (process == null)
            return null;

        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return new
                {
                    processId = process.Id,
                    hasExited = true,
                    exitCode = (int?)process.ExitCode,
                    privateMemoryBytes = (long?)null,
                    workingSetBytes = (long?)null,
                    handleCount = (int?)null,
                };
            }

            return new
            {
                processId = process.Id,
                hasExited = false,
                exitCode = (int?)null,
                privateMemoryBytes = (long?)process.PrivateMemorySize64,
                workingSetBytes = (long?)process.WorkingSet64,
                handleCount = (int?)process.HandleCount,
            };
        }
        catch (InvalidOperationException)
        {
            return new { processId = (int?)null, unavailable = true };
        }
    }
}
