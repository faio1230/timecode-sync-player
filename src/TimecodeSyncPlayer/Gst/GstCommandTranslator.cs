using System;
using System.Globalization;

namespace TimecodeSyncPlayer.Gst;

internal abstract record GstPlayerOperation;

internal sealed record GstLoadFileOperation(string Path, double? StartSeconds) : GstPlayerOperation;

internal sealed record GstSeekOperation(double Seconds, bool Relative) : GstPlayerOperation;

internal sealed record GstStopOperation : GstPlayerOperation
{
    public static readonly GstStopOperation Instance = new();
    private GstStopOperation() { }
}

internal sealed record GstFrameStepOperation : GstPlayerOperation
{
    public static readonly GstFrameStepOperation Instance = new();
    private GstFrameStepOperation() { }
}

/// <summary>解析不能・無視コマンド（osd 系など）。再生状態を変えない。</summary>
internal sealed record GstIgnoredOperation(string Command) : GstPlayerOperation;

/// <summary>
/// 既存の mpv command-string 経路 (MpvPlaybackCommandBuilder /
/// PlaybackOperationsCoordinator / MainWindow) で発行されるコマンドを
/// GStreamer バックエンドの操作へ翻訳する純粋関数。
/// 例外を投げず、不明コマンドは GstIgnoredOperation を返す。
/// </summary>
internal static class GstCommandTranslator
{
    public static GstPlayerOperation Translate(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new GstIgnoredOperation(string.Empty);

        string rest = command.Trim();

        // mpv 側の装飾前置詞は逐語的に落としてよい（OSD なし / プロgress bars なし）。
        while (true)
        {
            if (rest.StartsWith("no-osd ", StringComparison.Ordinal)) rest = rest[7..].TrimStart();
            else if (rest.StartsWith("no-osd-bar ", StringComparison.Ordinal)) rest = rest[11..].TrimStart();
            else break;
        }

        if (rest == "stop")
            return GstStopOperation.Instance;
        if (rest == "frame-step" || rest == "frame-step force")
            return GstFrameStepOperation.Instance;

        if (rest.StartsWith("loadfile ", StringComparison.Ordinal))
            return ParseLoadFile(rest["loadfile ".Length..]);

        if (rest.StartsWith("seek ", StringComparison.Ordinal))
            return ParseSeek(rest["seek ".Length..]);

        return new GstIgnoredOperation(command);
    }

    private static GstPlayerOperation ParseLoadFile(string args)
    {
        string s = args.Trim();
        if (s.Length == 0 || s[0] != '"')
            return new GstIgnoredOperation("loadfile " + args);

        int i = 1;
        var path = new System.Text.StringBuilder();
        bool closed = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char next = s[i + 1];
                if (next == '\\' || next == '"')
                {
                    path.Append(next);
                    i += 2;
                    continue;
                }
                path.Append(c);
                i++;
                continue;
            }
            if (c == '"')
            {
                closed = true;
                i++;
                break;
            }
            path.Append(c);
            i++;
        }
        if (!closed)
            return new GstIgnoredOperation("loadfile " + args);

        string tail = s[i..].Trim();

        // "replace" (mpv load file flags) - accept optional order:
        //   replace [-1] [start=SECONDS]
        double? start = null;
        foreach (string token in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("start=", StringComparison.Ordinal))
            {
                string raw = token[6..];
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double st))
                    return new GstIgnoredOperation("loadfile " + args);
                start = Math.Max(st, 0.0);
            }
        }

        return new GstLoadFileOperation(path.ToString(), start);
    }

    private static GstPlayerOperation ParseSeek(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return new GstIgnoredOperation("seek " + args);
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return new GstIgnoredOperation("seek " + args);

        string flags = parts[1];
        bool relative = flags.StartsWith("relative", StringComparison.Ordinal);
        bool absolute = flags.StartsWith("absolute", StringComparison.Ordinal);
        if (!relative && !absolute)
            return new GstIgnoredOperation("seek " + args);

        return new GstSeekOperation(seconds, relative);
    }
}
