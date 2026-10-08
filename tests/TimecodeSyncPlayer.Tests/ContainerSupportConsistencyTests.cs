using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 試験基盤の束の 6（docs/design/test-infra-2026-10.md の 6・9 節）: 形式の表と同梱の突き合わせ。
/// 配布物に入れる GStreamer のプラグイン（scripts/package-release.ps1 の $gstPluginDlls）、
/// shim の拡張子の表（native/gst-shim/src/tcs_gstreamer.cpp の kDemuxByExtension）、
/// アプリのファイルの選択の型（MainWindow.xaml.cs の Filter）、
/// 現場準備ガイド（docs/USER-MANUAL.md の「1-6. 容器の対応」の表）の 4 つを読み、食い違いを落とす。
/// </summary>
public class ContainerSupportConsistencyTests
{
    /// <summary>demux の要素の名前 → それを持つプラグインの DLL。shim の表に新しい demux が入ったらここに足す。</summary>
    private static readonly IReadOnlyDictionary<string, string> DemuxPluginDll = new Dictionary<string, string>
    {
        ["qtdemux"] = "gstisomp4.dll",
        ["matroskademux"] = "gstmatroska.dll",
        ["tsdemux"] = "gstmpegtsdemux.dll",
        ["mxfdemux"] = "gstmxf.dll",
        ["avidemux"] = "gstavi.dll",
    };

    /// <summary>
    /// ファイルの選択の型に出ていて、同梱に demux が無いことを既知とする拡張子。
    /// 次の製品の版で型から外す（docs/design/v0.6.4-inventory.md の #46）。外したらここからも外す。
    /// </summary>
    private static readonly string[] KnownUnbundledInFileDialog = [".avi", ".mkv"];

    private const string ManualSectionHeading = "### 1-6. 容器の対応";

    [Fact]
    public void FileDialogExtensions_WithoutBundledDemux_AreExactlyTheKnownOnes()
    {
        IReadOnlyDictionary<string, string> shim = ReadShimTable(out _);
        IReadOnlySet<string> bundled = ReadBundledPluginDlls();
        IReadOnlyList<string> dialog = ReadFileDialogExtensions();

        dialog.Should().OnlyContain(ext => shim.ContainsKey(ext),
            "ファイルの選択の型に出す拡張子は shim の表にある（表に無いと decodebin の最後の手に回る）");

        string[] unbundled = dialog
            .Where(ext => !bundled.Contains(PluginFor(shim[ext])))
            .OrderBy(ext => ext, StringComparer.Ordinal)
            .ToArray();

        unbundled.Should().Equal(KnownUnbundledInFileDialog.OrderBy(ext => ext, StringComparer.Ordinal),
            "選べるのに同梱に demux が無い拡張子は既知の 2 つだけ（増えたら文書と型を見直す）");
    }

    [Fact]
    public void ManualUnsupportedList_MatchesShimExtensionsWithoutBundledDemux()
    {
        IReadOnlyDictionary<string, string> shim = ReadShimTable(out _);
        IReadOnlySet<string> bundled = ReadBundledPluginDlls();
        (IReadOnlyList<string> openable, IReadOnlyList<string> unopenable) = ReadManualContainerTable();

        string[] unbundledInShim = shim
            .Where(kv => !bundled.Contains(PluginFor(kv.Value)))
            .Select(kv => kv.Key)
            .OrderBy(ext => ext, StringComparer.Ordinal)
            .ToArray();

        unopenable.OrderBy(ext => ext, StringComparer.Ordinal).Should().Equal(unbundledInShim,
            "現場準備ガイドの「開けない」は、shim の表で同梱に demux が無い拡張子と一致する");

        openable.Should().OnlyContain(ext => shim.ContainsKey(ext) && bundled.Contains(PluginFor(shim[ext])),
            "現場準備ガイドの「開ける」は、shim の表にあり、同梱に demux がある");
        openable.Should().Contain([".mp4", ".mov"], "推奨の容器は mp4 と mov");
    }

    [Fact]
    public void ShimTableRows_WithoutBundledDemux_CarryTheNote()
    {
        IReadOnlyDictionary<string, string> shim = ReadShimTable(out IReadOnlyDictionary<string, string> comments);
        IReadOnlySet<string> bundled = ReadBundledPluginDlls();

        foreach ((string ext, string demux) in shim)
        {
            bool isBundled = bundled.Contains(PluginFor(demux));
            string note = $"同梱の GStreamer に {demux} は入っていない";
            if (isBundled)
                comments[ext].Should().NotContain("同梱の GStreamer に", $"{ext} の demux（{demux}）は同梱にある");
            else
                comments[ext].Should().Contain(note, $"{ext} の demux（{demux}）は同梱に無いので、表の行に注記がいる");
        }
    }

    private static string PluginFor(string demux)
    {
        DemuxPluginDll.Should().ContainKey(demux, "shim の表の demux は、試験の対応の表（DemuxPluginDll）にある");
        return DemuxPluginDll[demux];
    }

    private static IReadOnlySet<string> ReadBundledPluginDlls()
    {
        string text = File.ReadAllText(RepoPath("scripts", "package-release.ps1"));
        Match block = Regex.Match(text, @"\$gstPluginDlls\s*=\s*@\((?<body>.*?)\)", RegexOptions.Singleline);
        block.Success.Should().BeTrue("package-release.ps1 に $gstPluginDlls の一覧がある");
        HashSet<string> dlls = Regex.Matches(block.Groups["body"].Value, "\"(?<dll>[^\"]+\\.dll)\"")
            .Select(m => m.Groups["dll"].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        dlls.Should().Contain("gstisomp4.dll", "読み取りの確かめ（mp4・mov の demux は同梱にある）");
        return dlls;
    }

    /// <summary>shim の表の拡張子 → demux。comments には各行の、行の後ろのコメント（無ければ空）。</summary>
    private static IReadOnlyDictionary<string, string> ReadShimTable(out IReadOnlyDictionary<string, string> comments)
    {
        string text = File.ReadAllText(RepoPath("native", "gst-shim", "src", "tcs_gstreamer.cpp"));
        Match block = Regex.Match(text, @"kDemuxByExtension\[\]\s*=\s*\{(?<body>.*?)\n\};", RegexOptions.Singleline);
        block.Success.Should().BeTrue("tcs_gstreamer.cpp に kDemuxByExtension の表がある");

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in block.Groups["body"].Value.Split('\n'))
        {
            Match row = Regex.Match(line, "^\\s*\\{\\s*\"(?<ext>\\.[A-Za-z0-9]+)\"\\s*,\\s*\"(?<demux>\\w+)\"\\s*\\}\\s*,(?<rest>.*)$");
            if (!row.Success)
                continue;
            string ext = row.Groups["ext"].Value.ToLowerInvariant();
            table[ext] = row.Groups["demux"].Value;
            notes[ext] = row.Groups["rest"].Value.Trim();
        }
        table.Should().ContainKey(".mp4", "読み取りの確かめ（表の行を読めている）");
        comments = notes;
        return table;
    }

    private static IReadOnlyList<string> ReadFileDialogExtensions()
    {
        string text = File.ReadAllText(RepoPath("src", "TimecodeSyncPlayer", "MainWindow.xaml.cs"));
        MatchCollection filters = Regex.Matches(text, "Filter\\s*=\\s*\"動画\\|(?<exts>[^|\"]+)\\|");
        filters.Should().NotBeEmpty("MainWindow.xaml.cs に動画のファイルの選択の型がある");

        string[][] sets = filters
            .Select(m => m.Groups["exts"].Value.Split(';')
                .Select(p => p.Trim().TrimStart('*').ToLowerInvariant())
                .OrderBy(ext => ext, StringComparer.Ordinal)
                .ToArray())
            .ToArray();
        foreach (string[] set in sets.Skip(1))
            set.Should().Equal(sets[0], "ファイルの選択の型はどの画面でも同じ");
        return sets[0];
    }

    private static (IReadOnlyList<string> Openable, IReadOnlyList<string> Unopenable) ReadManualContainerTable()
    {
        string[] lines = File.ReadAllLines(RepoPath("docs", "USER-MANUAL.md"));
        int start = Array.FindIndex(lines, l => l.Trim() == ManualSectionHeading);
        start.Should().BeGreaterThanOrEqualTo(0, $"現場準備ガイドに「{ManualSectionHeading}」の節がある");

        var openable = new List<string>();
        var unopenable = new List<string>();
        for (int i = start + 1; i < lines.Length && !lines[i].StartsWith('#'); i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith('|'))
                continue;
            string[] cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 3)
                continue;
            List<string> target;
            if (cells[2].StartsWith("開けない", StringComparison.Ordinal))
                target = unopenable;
            else if (cells[2] == "開ける")
                target = openable;
            else
                continue;
            target.AddRange(Regex.Matches(cells[1], @"\.[A-Za-z0-9]+").Select(m => m.Value.ToLowerInvariant()));
        }
        openable.Should().NotBeEmpty("容器の対応の表に「開ける」の行がある");
        unopenable.Should().NotBeEmpty("容器の対応の表に「開けない」の行がある");
        return (openable, unopenable);
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimecodeSyncPlayer.slnx")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("リポジトリルートが見つかりません。");
        return Path.Combine([dir.FullName, .. parts]);
    }
}
