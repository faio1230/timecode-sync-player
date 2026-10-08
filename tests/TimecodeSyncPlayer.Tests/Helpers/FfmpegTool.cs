using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal sealed record FfmpegResolution(string Ffmpeg, string FfmpegSource, string Ffprobe, string FfprobeSource);

/// <summary>
/// v0.6.0 段 5b: 試験で素材を作る ffmpeg / ffprobe の解決。スクリプト（scripts\TcsFfmpeg.psm1）と同じ順:
/// ffmpeg は <c>TCS_FFMPEG</c>（ffmpeg.exe のフルパス）→ リポジトリの <c>tools\ffmpeg</c>（試験用に固定した版、
/// scripts\get-ffmpeg.ps1 で取る。試験基盤の 8）→ 既定のフォルダ（スクリプトの -FfmpegDir の既定と同じ
/// <c>%ProgramFiles%\ffmpeg\bin</c>。試験には引数が無いので既定だけ）→ PATH（最後の手）。
/// ffprobe は <c>TCS_FFPROBE</c> → 解決した ffmpeg と同じフォルダ → PATH。
/// 以前は名前（"ffmpeg"）で起動しており、開発機と検証機で PATH の先頭の版が逆向きにずれていた。
/// 試験基盤の 8: 見つけた ffmpeg の版が 6 未満なら、最初に <see cref="Ffmpeg"/> を使うところで止める
/// （検証機のまっさらな回で n5.0 を拾い、F-7 の HAP の素材が試験の中で作れず落ちたため）。
/// </summary>
internal static class FfmpegTool
{
    public const string FfmpegEnvironmentVariable = "TCS_FFMPEG";
    public const string FfprobeEnvironmentVariable = "TCS_FFPROBE";

    /// <summary>tools\ffmpeg の置き換え（自己試験の用。ふだんは設定しない）。scripts と同じ名前。</summary>
    public const string ToolsDirectoryEnvironmentVariable = "TCS_FFMPEG_TOOLS_DIR";

    /// <summary>これより古い版では試験を始めない（素材の一部、特に hap_mov が作れない）。</summary>
    public const int MinimumMajor = 6;

    /// <summary>既定のフォルダ（scripts の make-*-media.ps1 の -FfmpegDir の既定と同じ）。</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");

    /// <summary>リポジトリの tools\ffmpeg（scripts\get-ffmpeg.ps1 の置き場）。リポジトリが見つからなければ null。</summary>
    public static string? ToolsDirectory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(ToolsDirectoryEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimecodeSyncPlayer.slnx")))
                dir = dir.Parent;
            return dir is null ? null : Path.Combine(dir.FullName, "tools", "ffmpeg");
        }
    }

    private static readonly Lazy<FfmpegResolution> s_resolution = new(() =>
        Resolve(Environment.GetEnvironmentVariable, ToolsDirectory, DefaultDirectory, Environment.GetEnvironmentVariable("PATH")));

    private static readonly Lazy<string> s_versionLines = new(() => ReadVersionLines(s_resolution.Value.Ffmpeg));

    public static FfmpegResolution Current => s_resolution.Value;

    /// <summary>
    /// 素材を作る ffmpeg のフルパス。見つけた ffmpeg が 6 未満なら <see cref="InvalidOperationException"/> で止める
    /// （文言に場所・版・get-ffmpeg.ps1）。見つからないときは従来どおり名前を返し、起動の失敗で分かる。
    /// </summary>
    public static string Ffmpeg
    {
        get
        {
            if (VersionStopReason(Current, s_versionLines.Value) is { } stop)
                throw new InvalidOperationException(stop);
            return Current.Ffmpeg;
        }
    }

    /// <summary>試験基盤の 8: 始める前に止める理由（6 未満・版が読めない）。止めないときは null。</summary>
    public static string? StopReason => VersionStopReason(Current, s_versionLines.Value);

    public static string Ffprobe => Current.Ffprobe;

    /// <summary><c>ffmpeg -version</c> の 1 行目（起動できなければその理由）。</summary>
    public static string VersionLine => FirstLine(s_versionLines.Value);

    public static int Major => ParseMajor(s_versionLines.Value);

    /// <summary>版が 6 未満（または不明）なら警告の 1 行、そうでなければ null。</summary>
    public static string? OldVersionWarning(string versionLine, int major)
    {
        if (major >= MinimumMajor) return null;
        string shown = major < 0 ? "unknown" : major.ToString(CultureInfo.InvariantCulture);
        return $"ffmpeg-warning: major {shown} is older than {MinimumMajor} and made new media ({versionLine}). " +
               $"Point {FfmpegEnvironmentVariable} at a build {MinimumMajor} or later.";
    }

    /// <summary>
    /// 試験基盤の 8: 解決した ffmpeg で試験を始めてよいか。見つかった（名前でない）ffmpeg の版が 6 未満、
    /// または版が読めなければ止める理由を返す。見つからない（名前）ときは null（起動の失敗で分かる）。
    /// </summary>
    internal static string? VersionStopReason(FfmpegResolution resolution, string versionOutput)
    {
        if (resolution.FfmpegSource == "name") return null;
        int major = ParseMajor(versionOutput);
        if (major >= MinimumMajor) return null;
        string shown = major < 0 ? "unknown" : major.ToString(CultureInfo.InvariantCulture);
        return $"ffmpeg major {shown} is older than {MinimumMajor}: {resolution.Ffmpeg} ({resolution.FfmpegSource}, " +
               $"{FirstLine(versionOutput)}). Get the pinned test build with pwsh -File scripts\\get-ffmpeg.ps1 " +
               $"(tools\\ffmpeg), or point {FfmpegEnvironmentVariable} at a build {MinimumMajor} or later.";
    }

    internal static FfmpegResolution Resolve(Func<string, string?> getEnvironment, string? toolsDirectory, string? defaultDirectory, string? pathVariable)
    {
        string ffmpeg;
        string ffmpegSource;
        string? fromEnvironment = getEnvironment(FfmpegEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            if (!File.Exists(fromEnvironment))
                throw new InvalidOperationException($"{FfmpegEnvironmentVariable} does not point to a file: {fromEnvironment}");
            ffmpeg = Path.GetFullPath(fromEnvironment);
            ffmpegSource = "env:" + FfmpegEnvironmentVariable;
        }
        else if (!string.IsNullOrEmpty(toolsDirectory) && File.Exists(Path.Combine(toolsDirectory, "ffmpeg.exe")))
        {
            ffmpeg = Path.GetFullPath(Path.Combine(toolsDirectory, "ffmpeg.exe"));
            ffmpegSource = "repo:tools\\ffmpeg";
        }
        else if (!string.IsNullOrEmpty(defaultDirectory) && File.Exists(Path.Combine(defaultDirectory, "ffmpeg.exe")))
        {
            ffmpeg = Path.GetFullPath(Path.Combine(defaultDirectory, "ffmpeg.exe"));
            ffmpegSource = "default-dir";
        }
        else if (FindOnPath("ffmpeg.exe", pathVariable) is { } onPath)
        {
            ffmpeg = onPath;
            ffmpegSource = "PATH";
        }
        else
        {
            // 見つからないときは従来どおり名前で起動させ、起動の失敗をそのまま出す。
            ffmpeg = "ffmpeg";
            ffmpegSource = "name";
        }

        string ffprobe;
        string ffprobeSource;
        string? probeFromEnvironment = getEnvironment(FfprobeEnvironmentVariable);
        string? probeNextToFfmpeg = Path.IsPathRooted(ffmpeg)
            ? Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe")
            : null;
        if (!string.IsNullOrWhiteSpace(probeFromEnvironment))
        {
            if (!File.Exists(probeFromEnvironment))
                throw new InvalidOperationException($"{FfprobeEnvironmentVariable} does not point to a file: {probeFromEnvironment}");
            ffprobe = Path.GetFullPath(probeFromEnvironment);
            ffprobeSource = "env:" + FfprobeEnvironmentVariable;
        }
        else if (probeNextToFfmpeg is not null && File.Exists(probeNextToFfmpeg))
        {
            ffprobe = probeNextToFfmpeg;
            ffprobeSource = "next-to-ffmpeg";
        }
        else if (FindOnPath("ffprobe.exe", pathVariable) is { } probeOnPath)
        {
            ffprobe = probeOnPath;
            ffprobeSource = "PATH";
        }
        else
        {
            ffprobe = "ffprobe";
            ffprobeSource = "name";
        }
        return new FfmpegResolution(ffmpeg, ffmpegSource, ffprobe, ffprobeSource);
    }

    /// <summary>
    /// 版の番号。リリース版は "ffmpeg version 4.2.3" / "n5.0"、git のビルドは "N-109850-g…" で番号が無いので
    /// libavcodec の番号で決める（58 = 4.x、59 = 5.x、60 = 6.x、61 = 7.x、62 = 8.x）。不明は -1。
    /// </summary>
    internal static int ParseMajor(string versionOutput)
    {
        Match release = Regex.Match(FirstLine(versionOutput), @"^ffmpeg version n?(\d+)\.");
        if (release.Success) return int.Parse(release.Groups[1].Value, CultureInfo.InvariantCulture);
        Match lavc = Regex.Match(versionOutput, @"^\s*libavcodec\s+(\d+)\.", RegexOptions.Multiline);
        if (lavc.Success)
        {
            int major = int.Parse(lavc.Groups[1].Value, CultureInfo.InvariantCulture);
            if (major >= 58) return major - 54;
        }
        return -1;
    }

    /// <summary>
    /// 素材のフォルダのサイドカー（ffmpeg-version.txt）。スクリプトと同じ書式: "&lt;name&gt; | &lt;版の 1 行目&gt; | made &lt;UTC&gt;"。
    /// 既にある行は残し、同じ名前は作り直した版で置き換える。
    /// </summary>
    public static string WriteSidecar(string directory, IEnumerable<string> madeNames, string versionLine)
    {
        string path = Path.Combine(directory, "ffmpeg-version.txt");
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                int index = line.IndexOf(" | ", StringComparison.Ordinal);
                if (line.StartsWith('#') || index < 0) continue;
                entries[line[..index]] = line[(index + 3)..];
            }
        }
        string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (string name in madeNames)
            entries[name] = $"{versionLine} | made {stamp}";
        var lines = new List<string>
        {
            "# ffmpeg that made each file (tests Helpers\\FfmpegTool.cs). <name> | <ffmpeg -version first line> | made <UTC>",
        };
        lines.AddRange(entries.Select(entry => $"{entry.Key} | {entry.Value}"));
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
        return path;
    }

    private static string? FindOnPath(string fileName, string? pathVariable)
    {
        if (string.IsNullOrEmpty(pathVariable)) return null;
        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                // PATH に不正な文字の要素があれば飛ばす。
            }
        }
        return null;
    }

    private static string ReadVersionLines(string ffmpeg)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            info.ArgumentList.Add("-version");
            using Process process = Process.Start(info)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                E2EAppRunner.KillProcess(process);
                return "ffmpeg -version timed out";
            }
            error.Wait();
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "ffmpeg -version failed: " + ex.Message;
        }
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).Trim();
    }
}
