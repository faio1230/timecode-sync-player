using System.Diagnostics;
using System.IO;

namespace TimecodeSyncPlayer.Tests;

/// <summary>ffmpeg でテスト用動画を生成するヘルパー。アプリドメイン単位で一度だけ生成する。</summary>
internal static class TestVideoFactory
{
    private static string? _videoPath;
    private static readonly Dictionary<string, string> _variantPaths = [];
    private static readonly object _lock = new();

    /// <summary>ffmpeg が PATH にあるかを確認する。</summary>
    public static bool FfmpegAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg", "-nostdin -version")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            bool exited = p.WaitForExit(3000);
            return exited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// テスト動画のパスを返す。まだ生成されていなければ ffmpeg で生成する。
    /// </summary>
    /// <returns>生成された .mp4 の絶対パス</returns>
    public static string GetOrCreate()
    {
        lock (_lock)
        {
            if (_videoPath != null && File.Exists(_videoPath))
                return _videoPath;

            string dir  = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "test_clip.mp4");

            // 既存ファイルを削除してから生成
            if (File.Exists(path)) File.Delete(path);

            var psi = new ProcessStartInfo("ffmpeg",
                $"-nostdin -y -f lavfi -i testsrc=duration=20:size=1280x720:rate=30 -c:v libx264 -t 20 \"{path}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
                CreateNoWindow         = true,
            };

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("ffmpeg の起動に失敗しました。");
            bool exited = p.WaitForExit(60_000);

            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("ffmpeg がタイムアウトしました（60秒）。");
            }

            if (p.ExitCode != 0 || !File.Exists(path))
                throw new InvalidOperationException(
                    $"ffmpeg でテスト動画の生成に失敗しました (exit={p.ExitCode})。");

            _videoPath = path;
            return _videoPath;
        }
    }

    public static string GetOrCreateVariant(string name)
    {
        lock (_lock)
        {
            if (_variantPaths.TryGetValue(name, out string? existing) && File.Exists(existing))
                return existing;

            string safeName = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "variant";

            string dir = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"test_clip_{safeName}.mp4");

            if (File.Exists(path)) File.Delete(path);

            var psi = new ProcessStartInfo("ffmpeg",
                $"-nostdin -y -f lavfi -i smptebars=duration=20:size=1280x720:rate=30 -c:v libx264 -t 20 \"{path}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
                CreateNoWindow         = true,
            };

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("ffmpeg の起動に失敗しました。");
            bool exited = p.WaitForExit(60_000);

            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("ffmpeg がタイムアウトしました（60秒）。");
            }

            if (p.ExitCode != 0 || !File.Exists(path))
                throw new InvalidOperationException(
                    $"ffmpeg でテスト動画variantの生成に失敗しました (exit={p.ExitCode})。");

            _variantPaths[name] = path;
            return path;
        }
    }

    /// <summary>生成したテスト動画を削除する（テスト終了後に呼ぶ）。</summary>
    public static void Cleanup()
    {
        lock (_lock)
        {
            if (_videoPath != null && File.Exists(_videoPath))
                File.Delete(_videoPath);
            _videoPath = null;

            foreach (string path in _variantPaths.Values)
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            _variantPaths.Clear();
        }
    }
}
