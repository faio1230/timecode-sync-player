using System.Diagnostics;
using System.IO;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// T10: E2E で起動したアプリの stdout / stderr を run ディレクトリへ保存する。
/// shim の load.attempt（stderr）はアプリ実行時はどこにも残らないため、
/// TIMECODE_ACCURACY_REPORT_DIR があるときだけパイプを張り、非同期で読み続ける
/// （読み取りを止めるとアプリ側の書き込みが詰まる）。
/// </summary>
internal static class AppStreamCapture
{
    public const string ReportDirectoryEnvironmentVariable = "TIMECODE_ACCURACY_REPORT_DIR";
    public const string StdoutFileName = "app-stdout.txt";
    public const string StderrFileName = "app-stderr.txt";

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable(ReportDirectoryEnvironmentVariable));

    /// <summary>Process.Start の前に呼ぶ。子の stdout / stderr をパイプへ回す。</summary>
    public static void Configure(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
    }

    /// <summary>Process.Start の直後に呼ぶ。run ディレクトリへ 2 本のファイルとして書き出す。</summary>
    public static void Attach(Process process)
    {
        string directory = Environment.GetEnvironmentVariable(ReportDirectoryEnvironmentVariable)!;
        Directory.CreateDirectory(directory);

        var stdout = new StreamWriter(Path.Combine(directory, StdoutFileName), append: false) { AutoFlush = true };
        var stderr = new StreamWriter(Path.Combine(directory, StderrFileName), append: false) { AutoFlush = true };

        process.OutputDataReceived += (_, e) => WriteLine(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => WriteLine(stderr, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static void WriteLine(StreamWriter writer, string? line)
    {
        if (line == null)
            return;
        lock (writer)
            writer.WriteLine(line);
    }
}
