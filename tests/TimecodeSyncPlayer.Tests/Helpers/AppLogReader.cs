using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// v0.6.4 段 4（設計書 2-3、#21）: E2E がアプリのログ（&lt;exe&gt;\logs\timecodesyncplayer-*.log）を読む共通の口。
/// Serilog は日次でファイルを切り替える（rollingInterval Day）ので、0 時をまたぐ回の行は前日と当日の 2 ファイルに
/// 分かれる。最後に書かれた 1 ファイルだけを読むと前日の行を落とす（検証機の v0.5.5 の R-5 で、23:59:59 の着地の行を
/// 落として landingSeeks=0 と誤判定）。sinceLocal の日付の 0 時以降に書かれたファイルをすべて、名前（日付）の昇順に
/// 読み、行の時刻が sinceLocal 以降の行だけを返す。時刻で始まらない行（例外の続きの行など）は返さない。
/// </summary>
internal static class AppLogReader
{
    private static readonly Regex TimestampPattern = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");

    /// <summary>アプリの exe のフォルダからログのフォルダを返す（ログは実際に起動した exe の隣に出る）。</summary>
    public static string LogDirectoryForExeDirectory(string exeDirectory) => Path.Combine(exeDirectory, "logs");

    /// <summary>アプリの exe のパスからログのフォルダを返す。</summary>
    public static string LogDirectoryForExe(string exePath) =>
        LogDirectoryForExeDirectory(Path.GetDirectoryName(exePath)!);

    /// <summary>行の時刻が sinceLocal 以降の行を、日付の古いファイルから順に返す。</summary>
    public static IEnumerable<string> ReadLinesSince(string logDirectory, DateTime sinceLocal)
    {
        if (!Directory.Exists(logDirectory)) yield break;
        IEnumerable<FileInfo> files = new DirectoryInfo(logDirectory).GetFiles("timecodesyncplayer-*.log")
            .Where(file => file.LastWriteTime >= sinceLocal.Date)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase);

        foreach (FileInfo file in files)
        {
            string text;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();

            foreach (string line in text.Split('\n'))
            {
                Match timestamp = TimestampPattern.Match(line);
                if (!timestamp.Success ||
                    !DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime at) ||
                    at < sinceLocal)
                    continue;
                yield return line;
            }
        }
    }

    /// <summary><see cref="ReadLinesSince"/> の行を改行でつないで返す（Contains で文言を探す呼び出し向け）。</summary>
    public static string ReadTextSince(string logDirectory, DateTime sinceLocal) =>
        string.Join('\n', ReadLinesSince(logDirectory, sinceLocal));
}
