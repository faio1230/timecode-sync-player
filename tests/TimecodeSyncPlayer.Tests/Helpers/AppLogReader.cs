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

    /// <summary>行の先頭の時刻（ローカルとして解釈）。時刻で始まらない行は false。</summary>
    internal static bool TryReadTimestamp(string line, out DateTime at)
    {
        at = default;
        Match timestamp = TimestampPattern.Match(line);
        return timestamp.Success &&
               DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    /// <summary>この日の 0 時以降に書かれたログのファイルを、名前（日付）の昇順に返す。</summary>
    internal static IEnumerable<FileInfo> FilesSince(string logDirectory, DateTime sinceLocal) =>
        new DirectoryInfo(logDirectory).GetFiles("timecodesyncplayer-*.log")
            .Where(file => file.LastWriteTime >= sinceLocal.Date)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>アプリの exe のフォルダからログのフォルダを返す（ログは実際に起動した exe の隣に出る）。</summary>
    public static string LogDirectoryForExeDirectory(string exeDirectory) => Path.Combine(exeDirectory, "logs");

    /// <summary>アプリの exe のパスからログのフォルダを返す。</summary>
    public static string LogDirectoryForExe(string exePath) =>
        LogDirectoryForExeDirectory(Path.GetDirectoryName(exePath)!);

    /// <summary>
    /// 行の時刻が sinceLocal 以降（同じ時刻を含む）の行を、日付の古いファイルから順に返す。
    /// 行の時刻のオフセット（+09:00 など）は読まずにローカルとして解釈する。ログを書いた機械で読む前提
    /// （開始の時刻も同じ機械の DateTime.Now）。
    /// </summary>
    public static IEnumerable<string> ReadLinesSince(string logDirectory, DateTime sinceLocal)
    {
        if (!Directory.Exists(logDirectory)) yield break;
        foreach (FileInfo file in FilesSince(logDirectory, sinceLocal))
        {
            string text;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                text = reader.ReadToEnd();

            foreach (string line in text.Split('\n'))
            {
                if (!TryReadTimestamp(line, out DateTime at) || at < sinceLocal)
                    continue;
                yield return line;
            }
        }
    }

    /// <summary><see cref="ReadLinesSince"/> の行を改行でつないで返す（Contains で文言を探す呼び出し向け）。</summary>
    public static string ReadTextSince(string logDirectory, DateTime sinceLocal) =>
        string.Join('\n', ReadLinesSince(logDirectory, sinceLocal));
}

/// <summary>
/// v0.6.4（設計書 9-2）: 待ちのループ向けの、前回読んだ位置からの続きの読み。ファイルごとに読んだバイトの位置を持ち、
/// ReadNew で新しく書かれた分だけを読む（毎回ファイル全体を読み直すと、日をまたいで育ったログで待ちが遅くなった）。
/// 読むファイルと行の選び方は <see cref="AppLogReader.ReadLinesSince"/> と同じ（sinceLocal の日付の 0 時以降に書かれた
/// ファイルを名前の昇順、行の時刻が sinceLocal 以降の行だけ、時刻で始まらない行は読まない）。新しい日のファイルが
/// できれば、次の ReadNew から位置 0 で読み始める。違いは、改行の前の書きかけの行を返さず、改行が届いてから返すこと。
/// ファイルが縮んだ（置き換えられた）ときは頭から読み直す。読みは 1 つずつ（ロックの内側）。
/// </summary>
internal sealed class AppLogTail
{
    private readonly string _logDirectory;
    private readonly Dictionary<string, long> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTime At, string Line)> _lines = new();
    private readonly object _gate = new();

    public AppLogTail(string logDirectory, DateTime sinceLocal)
    {
        _logDirectory = logDirectory;
        SinceLocal = sinceLocal;
    }

    public DateTime SinceLocal { get; }

    /// <summary>新しく書かれた行を読んで積み、その行を返す。</summary>
    public IReadOnlyList<string> ReadNew()
    {
        lock (_gate)
        {
            var added = new List<string>();
            if (!Directory.Exists(_logDirectory)) return added;
            foreach (FileInfo file in AppLogReader.FilesSince(_logDirectory, SinceLocal))
            {
                _positions.TryGetValue(file.FullName, out long position);
                byte[] bytes;
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < position) position = 0;   // 置き換えられた
                    if (stream.Length == position) continue;
                    stream.Position = position;
                    bytes = new byte[stream.Length - position];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read < bytes.Length) Array.Resize(ref bytes, read);
                }
                // 改行（0x0A）までの完全な行だけを読む。UTF-8 では 0x0A が文字の途中に現れない。
                int lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
                if (lastNewline < 0) continue;
                _positions[file.FullName] = position + lastNewline + 1;
                string text = System.Text.Encoding.UTF8.GetString(bytes, 0, lastNewline);
                foreach (string line in text.Split('\n'))
                {
                    if (!AppLogReader.TryReadTimestamp(line, out DateTime at) || at < SinceLocal)
                        continue;
                    _lines.Add((at, line));
                    added.Add(line);
                }
            }
            return added;
        }
    }

    /// <summary>これまでに読んだ行（読んだ順）。</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines.Select(entry => entry.Line).ToList(); }
    }

    /// <summary>これまでに読んだ行のうち、行の時刻が sinceLocal 以降の行（sinceLocal は SinceLocal 以降であること）。</summary>
    public IReadOnlyList<string> LinesSince(DateTime sinceLocal)
    {
        lock (_gate) return _lines.Where(entry => entry.At >= sinceLocal).Select(entry => entry.Line).ToList();
    }

    /// <summary>新しく書かれた分を読んでから、これまでの行を改行でつないで返す（Contains で文言を探す待ち向け）。</summary>
    public string ReadText()
    {
        ReadNew();
        return string.Join('\n', Lines);
    }
}
