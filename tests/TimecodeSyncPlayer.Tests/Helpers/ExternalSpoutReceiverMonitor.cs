using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal sealed record ExternalSpoutReceiverSummary(
    bool SummaryFound,
    bool CompletedNormally,
    int? ExitCode,
    bool TimedOut,
    int Errors,
    bool ConnectedEver,
    bool FrameCounterAvailable,
    long Polls,
    long ReceiveFailures,
    long UniqueFrames,
    long ObservedIntervals,
    long CounterJumps,
    long MissedSenderFrames,
    long CounterResets,
    long Disconnects,
    long MetadataChanges,
    long GapsAtLeast100Ms,
    long GapsAtLeast250Ms,
    long GapsAtLeast500Ms,
    double MaxGapMilliseconds,
    long ReceiveAtLeast10Ms,
    long ReceiveAtLeast50Ms,
    long ReceiveAtLeast100Ms,
    double MaxReceiveMilliseconds,
    long PixelSamples,
    long BlackPixelSamples,
    long UnchangedPixelSamples,
    long ContentChanges,
    long MaxUnchangedSampleStreak,
    double MaxUnchangedMilliseconds,
    long NonBlackRunsAtLeast100Ms,
    long NonBlackRunsAtLeast250Ms,
    long NonBlackRunsAtLeast500Ms,
    double MaxNonBlackUnchangedMilliseconds,
    long MaxNonBlackUnchangedQpc,
    long ReadbackAtLeast10Ms,
    long ReadbackAtLeast50Ms,
    long ReadbackAtLeast100Ms,
    double MaxGpuReadbackMilliseconds,
    double WallSeconds,
    double CpuSeconds);

internal static class ExternalSpoutReceiverSummaryParser
{
    public static ExternalSpoutReceiverSummary Parse(string path, int? exitCode, bool timedOut)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonElement? summary = null;
        if (File.Exists(path))
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (root.TryGetProperty("event", out JsonElement eventName) &&
                        string.Equals(eventName.GetString(), "summary", StringComparison.Ordinal))
                        summary = root.Clone();
                }
                catch (JsonException)
                {
                    // A forcibly terminated process can leave one incomplete last line. Absence of
                    // a valid summary is reported by SummaryFound/CompletedNormally.
                }
            }
        }

        if (summary is not { } value)
            return Empty(exitCode, timedOut);

        int errors = Int(value, "errors");
        bool connected = Boolean(value, "connectedEver");
        bool counter = Boolean(value, "frameCounterAvailable");
        long uniqueFrames = Long(value, "uniqueFrames");
        bool completed = !timedOut && exitCode == 0 && errors == 0 && connected && counter && uniqueFrames >= 2;
        return new ExternalSpoutReceiverSummary(
            true, completed, exitCode, timedOut, errors, connected, counter,
            Long(value, "polls"), Long(value, "receiveFailures"), uniqueFrames,
            Long(value, "observedIntervals"), Long(value, "counterJumps"),
            Long(value, "missedSenderFrames"), Long(value, "counterResets"),
            Long(value, "disconnects"), Long(value, "metadataChanges"),
            Long(value, "gapsAtLeast100Ms"), Long(value, "gapsAtLeast250Ms"),
            Long(value, "gapsAtLeast500Ms"), Number(value, "maxGapMs"),
            Long(value, "receiveAtLeast10Ms"), Long(value, "receiveAtLeast50Ms"),
            Long(value, "receiveAtLeast100Ms"), Number(value, "maxReceiveMs"),
            Long(value, "pixelSamples"), Long(value, "blackPixelSamples"),
            Long(value, "unchangedPixelSamples"), Long(value, "contentChanges"),
            Long(value, "maxUnchangedSampleStreak"), Number(value, "maxUnchangedMs"),
            Long(value, "nonBlackRunsAtLeast100Ms"), Long(value, "nonBlackRunsAtLeast250Ms"),
            Long(value, "nonBlackRunsAtLeast500Ms"), Number(value, "maxNonBlackUnchangedMs"),
            Long(value, "maxNonBlackUnchangedQpc"),
            Long(value, "readbackAtLeast10Ms"),
            Long(value, "readbackAtLeast50Ms"), Long(value, "readbackAtLeast100Ms"),
            Number(value, "maxGpuReadbackMs"), Number(value, "wallSeconds"),
            Number(value, "cpuSeconds"));
    }

    private static ExternalSpoutReceiverSummary Empty(int? exitCode, bool timedOut) =>
        new(
            SummaryFound: false,
            CompletedNormally: false,
            ExitCode: exitCode,
            TimedOut: timedOut,
            Errors: 0,
            ConnectedEver: false,
            FrameCounterAvailable: false,
            Polls: 0,
            ReceiveFailures: 0,
            UniqueFrames: 0,
            ObservedIntervals: 0,
            CounterJumps: 0,
            MissedSenderFrames: 0,
            CounterResets: 0,
            Disconnects: 0,
            MetadataChanges: 0,
            GapsAtLeast100Ms: 0,
            GapsAtLeast250Ms: 0,
            GapsAtLeast500Ms: 0,
            MaxGapMilliseconds: 0.0,
            ReceiveAtLeast10Ms: 0,
            ReceiveAtLeast50Ms: 0,
            ReceiveAtLeast100Ms: 0,
            MaxReceiveMilliseconds: 0.0,
            PixelSamples: 0,
            BlackPixelSamples: 0,
            UnchangedPixelSamples: 0,
            ContentChanges: 0,
            MaxUnchangedSampleStreak: 0,
            MaxUnchangedMilliseconds: 0.0,
            NonBlackRunsAtLeast100Ms: 0,
            NonBlackRunsAtLeast250Ms: 0,
            NonBlackRunsAtLeast500Ms: 0,
            MaxNonBlackUnchangedMilliseconds: 0.0,
            MaxNonBlackUnchangedQpc: 0,
            ReadbackAtLeast10Ms: 0,
            ReadbackAtLeast50Ms: 0,
            ReadbackAtLeast100Ms: 0,
            MaxGpuReadbackMilliseconds: 0.0,
            WallSeconds: 0.0,
            CpuSeconds: 0.0);

    private static long Long(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.TryGetInt64(out long result)
            ? result : 0;

    private static int Int(JsonElement value, string name) => checked((int)Long(value, name));

    private static double Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.TryGetDouble(out double result)
            ? result : 0.0;

    private static bool Boolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False && property.GetBoolean();
}

/// <summary>
/// 長時間試験とは別プロセスで Spout を受信する。出力には動画名・動画パスを含めない。
/// </summary>
internal sealed class ExternalSpoutReceiverMonitor : IDisposable
{
    public const string ExecutableEnvironmentVariable = "TCS_L2_SPOUT_RECEIVER_EXE";
    public const string SenderEnvironmentVariable = "TCS_L2_SPOUT_SENDER_NAME";
    public const string RequiredEnvironmentVariable = "TCS_L2_REQUIRE_EXTERNAL_SPOUT";
    public const string PixelSampleMillisecondsEnvironmentVariable = "TCS_L2_SPOUT_PIXEL_SAMPLE_MS";

    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    private readonly string _outputPath;
    private readonly string _stopPath;
    private readonly string _stdoutPath;
    private readonly string _stderrPath;
    private ExternalSpoutReceiverSummary? _summary;
    private bool _disposed;

    private ExternalSpoutReceiverMonitor(
        Process process,
        string outputPath,
        string stopPath,
        string stdoutPath,
        string stderrPath)
    {
        _process = process;
        _outputPath = outputPath;
        _stopPath = stopPath;
        _stdoutPath = stdoutPath;
        _stderrPath = stderrPath;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static bool IsRequired =>
        string.Equals(Environment.GetEnvironmentVariable(RequiredEnvironmentVariable), "1",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable(RequiredEnvironmentVariable), "true",
            StringComparison.OrdinalIgnoreCase);

    public static ExternalSpoutReceiverMonitor? StartFromEnvironment(
        string reportDirectory, double plannedSeconds)
    {
        string? executable = Environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(executable)) return null;
        string fullExecutable = Path.GetFullPath(executable);
        if (!File.Exists(fullExecutable))
            throw new FileNotFoundException("Spout continuity probe not found.", fullExecutable);

        string sender = Environment.GetEnvironmentVariable(SenderEnvironmentVariable)
            ?? SpoutDefaults.DefaultSenderName;
        string outputPath = Path.Combine(reportDirectory, "spout-receiver.jsonl");
        string stopPath = Path.Combine(reportDirectory, "spout-receiver.stop");
        string stdoutPath = Path.Combine(reportDirectory, "spout-receiver-stdout.txt");
        string stderrPath = Path.Combine(reportDirectory, "spout-receiver-stderr.txt");
        foreach (string path in new[] { outputPath, stopPath, stdoutPath, stderrPath })
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"Spout receiver output already exists: {Path.GetFileName(path)}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullExecutable,
            WorkingDirectory = Path.GetDirectoryName(fullExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--sender");
        startInfo.ArgumentList.Add(sender);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--stop-file");
        startInfo.ArgumentList.Add(stopPath);
        startInfo.ArgumentList.Add("--duration");
        startInfo.ArgumentList.Add(Math.Clamp(plannedSeconds + 120.0, 10.0, 46_800.0)
            .ToString("F3", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--poll-ms");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--sample-ms");
        int pixelSampleMilliseconds = int.TryParse(
            Environment.GetEnvironmentVariable(PixelSampleMillisecondsEnvironmentVariable),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int configuredSampleMilliseconds)
            ? Math.Clamp(configuredSampleMilliseconds, 16, 60_000)
            : 1000;
        startInfo.ArgumentList.Add(pixelSampleMilliseconds.ToString(CultureInfo.InvariantCulture));

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Spout continuity probe failed to start.");
        var monitor = new ExternalSpoutReceiverMonitor(
            process, outputPath, stopPath, stdoutPath, stderrPath);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(outputPath) && DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                    throw new InvalidOperationException(
                        $"Spout continuity probe exited during startup ({process.ExitCode}).");
                Thread.Sleep(25);
            }
            if (!File.Exists(outputPath))
                throw new TimeoutException("Spout continuity probe did not create its output in 5 seconds.");
            return monitor;
        }
        catch
        {
            monitor.Dispose();
            throw;
        }
    }

    public ExternalSpoutReceiverSummary Stop()
    {
        if (_summary is not null) return _summary;
        bool timedOut = false;
        try
        {
            if (!_process.HasExited)
            {
                using (new FileStream(_stopPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                if (!_process.WaitForExit(20_000))
                {
                    timedOut = true;
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5_000);
                }
            }
            else
            {
                _process.WaitForExit();
            }
        }
        finally
        {
            string stdout = _stdout.GetAwaiter().GetResult();
            string stderr = _stderr.GetAwaiter().GetResult();
            File.WriteAllText(_stdoutPath, stdout, new UTF8Encoding(false));
            File.WriteAllText(_stderrPath, stderr, new UTF8Encoding(false));
        }

        int? exitCode = null;
        try { if (_process.HasExited) exitCode = _process.ExitCode; }
        catch (InvalidOperationException) { }
        _summary = ExternalSpoutReceiverSummaryParser.Parse(_outputPath, exitCode, timedOut);
        return _summary;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); }
        finally { _process.Dispose(); }
    }
}
