using System.Diagnostics;
using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T10: 精度測定の run でアプリの stdout / stderr をファイルへ残すキャプチャの検証。
/// 実機を待たずに配管だけを確かめる（子プロセスは cmd.exe で代用）。
/// </summary>
public class AppStreamCaptureTests
{
    [Fact]
    public void Attach_WritesStdoutAndStderrToRunDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tcs-app-capture", Guid.NewGuid().ToString("N"));
        string variable = AppStreamCapture.ReportDirectoryEnvironmentVariable;
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, directory);

            AppStreamCapture.IsEnabled.Should().BeTrue();
            var startInfo = new ProcessStartInfo("cmd.exe", "/c echo capture-out & echo capture-err 1>&2")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            AppStreamCapture.Configure(startInfo);

            using Process process = Process.Start(startInfo)!;
            AppStreamCapture.Attach(process);
            process.WaitForExit(5000).Should().BeTrue();
            process.WaitForExit();   // 非同期の出力ハンドラ完了を待つ
            ReadShared(Path.Combine(directory, AppStreamCapture.StdoutFileName))
                .Should().Contain("capture-out");
            ReadShared(Path.Combine(directory, AppStreamCapture.StderrFileName))
                .Should().Contain("capture-err");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            try { Directory.Delete(directory, recursive: true); } catch { /* 一時ディレクトリ */ }
        }
    }

    // キャプチャの writer はテスト終了まで開いたままなので、読み取り側が書き込み共有を許す。
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void IsEnabled_IsFalseWithoutReportDirectory()
    {
        string variable = AppStreamCapture.ReportDirectoryEnvironmentVariable;
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);

            AppStreamCapture.IsEnabled.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
