using System.Text.Json;
using System.IO;

namespace TimecodeSyncPlayer;

/// <summary>
/// プロジェクトファイルの保存・読み込みを担当する。
/// </summary>
internal static class ProjectSerializer
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 現在読み込み中のプロジェクトファイルのパス。
    /// 相対パスの解決に使用される。
    /// </summary>
    public static string? ProjectPath { get; private set; }

    public static async Task SaveAsync(string filePath, PlaylistState playlist, SyncMode syncMode, GapBehavior gapBehavior)
    {
        string projectDirectory = Path.GetDirectoryName(filePath) ?? "";

        var project = new ProjectData
        {
            Version = CurrentVersion,
            SyncMode = syncMode,
            GapBehavior = gapBehavior,
            Tracks = playlist.Tracks.Select(t => new TrackData
            {
                Id = t.Id,
                FilePath = MakeRelativePath(t.FilePath, projectDirectory),
                Name = t.Name,
                MediaIn = t.MediaIn,
                MediaOut = t.MediaOut,
                TimelineOffset = t.TimelineOffset,
                MediaDuration = t.MediaDuration,
                SyncOffset = t.SyncOffset,
                FrameRate = t.FrameRate,
                IsEnabled = t.IsEnabled
            }).ToList()
        };

        string json = JsonSerializer.Serialize(project, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, System.Text.Encoding.UTF8);
    }

    public static async Task<ProjectData?> LoadAsync(string filePath)
    {
        ProjectPath = filePath;
        string projectDirectory = Path.GetDirectoryName(filePath) ?? "";

        string json = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8);
        
        // 後方互換性: timelineIn を timelineOffset として読み込む (JSONレベルの安全な移行)
        json = MigrateTimelineInToTimelineOffset(json);
        
        var project = JsonSerializer.Deserialize<ProjectData>(json, JsonOptions);
        if (project == null) return null;

        // バージョン検証
        if (project.Version > CurrentVersion)
        {
            Serilog.Log.Warning("プロジェクトファイルのバージョン({Version})がアプリケーションのバージョン({CurrentVersion})より新しいです。互換性がない可能性があります。",
                project.Version, CurrentVersion);
        }

        if (project.Version < 1)
        {
            Serilog.Log.Warning("プロジェクトファイルのバージョン({Version})が古いです。", project.Version);
        }

        var resolvedTracks = new List<TrackData>();
        foreach (var track in project.Tracks)
        {
            resolvedTracks.Add(track with { FilePath = ResolvePath(track.FilePath, projectDirectory) });
        }
        project = project with { Tracks = resolvedTracks };

        return project;
    }

    private static string MigrateTimelineInToTimelineOffset(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty("tracks", out var tracksElement) || tracksElement.ValueKind != JsonValueKind.Array)
                return json;
            
            bool needsMigration = false;
            foreach (var track in tracksElement.EnumerateArray())
            {
                if (track.TryGetProperty("timelineIn", out _))
                {
                    needsMigration = true;
                    break;
                }
            }
            
            if (!needsMigration)
                return json;
            
            Serilog.Log.Information("timelineIn→timelineOffset の移行を実行します");
            return json.Replace("\"timelineIn\"", "\"timelineOffset\"");
        }
        catch
        {
            // JSONパース失敗時は元の文字列のまま（後続のDeserializeでエラーハンドリングされる）
            return json;
        }
    }

    public static void ApplyToPlaylist(ProjectData project, PlaylistState playlist)
    {
        playlist.Clear();

        if (project.Tracks == null)
        {
            Serilog.Log.Warning("プロジェクトデータにトラック情報がありません");
            return;
        }

        foreach (var trackData in project.Tracks)
        {
            if (string.IsNullOrEmpty(trackData.FilePath))
            {
                Serilog.Log.Warning("プロジェクトトラックのファイルパスが空です: {Name}", trackData.Name);
                continue;
            }

            if (trackData.FrameRate is <= 0 or > 120)
            {
                Serilog.Log.Warning("プロジェクトトラックのフレームレートが無効です: {Name} FrameRate={FrameRate}", trackData.Name, trackData.FrameRate);
                continue;
            }

            if (trackData.TimelineOffset < TimeSpan.Zero)
            {
                Serilog.Log.Warning("プロジェクトトラックのタイムラインオフセットが無効です: {Name} Offset={Offset}", trackData.Name, trackData.TimelineOffset);
                continue;
            }

            if (trackData.MediaDuration < TimeSpan.Zero)
            {
                Serilog.Log.Warning("プロジェクトトラックのメディア長が無効です: {Name} Duration={Duration}", trackData.Name, trackData.MediaDuration);
                continue;
            }

            if (trackData.MediaIn < TimeSpan.Zero)
            {
                Serilog.Log.Warning("負のMediaInをスキップ: {MediaIn}", trackData.MediaIn);
                continue;
            }

            string projectDirectory = Path.GetDirectoryName(ProjectPath ?? "") ?? "";
            string resolvedPath = ResolvePath(trackData.FilePath, projectDirectory);
            if (!File.Exists(resolvedPath))
            {
                Serilog.Log.Warning("プロジェクトトラックのファイルが見つかりません: {Path} (スキップ)", resolvedPath);
                continue;
            }

            var name = trackData.Name ?? "無名トラック";

            playlist.Tracks.Add(new PlaylistTrack(
                Id: trackData.Id,
                FilePath: resolvedPath,
                Name: name,
                MediaIn: trackData.MediaIn,
                MediaOut: trackData.MediaOut,
                TimelineOffset: trackData.TimelineOffset,
                MediaDuration: trackData.MediaDuration,
                SyncOffset: trackData.SyncOffset,
                FrameRate: trackData.FrameRate,
                IsEnabled: trackData.IsEnabled
            ));
        }

        if (playlist.Tracks.Count > 0)
            playlist.Select(0);
    }

    /// <summary>
    /// 絶対パスをプロジェクトファイルからの相対パスに変換する。
    /// 異なるドライブの場合は絶対パスのまま返す。
    /// </summary>
    private static string MakeRelativePath(string absolutePath, string projectDirectory)
    {
        if (string.IsNullOrEmpty(absolutePath) || string.IsNullOrEmpty(projectDirectory))
            return absolutePath;

        try
        {
            string fullProjectDir = Path.GetFullPath(projectDirectory);
            string fullFilePath = Path.GetFullPath(absolutePath);

            Uri projectUri = new Uri(fullProjectDir + Path.DirectorySeparatorChar);
            Uri fileUri = new Uri(fullFilePath);

            if (projectUri.Scheme != fileUri.Scheme)
                return absolutePath;

            string relative = Uri.UnescapeDataString(projectUri.MakeRelativeUri(fileUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);

            return string.IsNullOrEmpty(relative) ? absolutePath : relative;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "MakeRelativePath failed for {Path} in {Dir}", absolutePath, projectDirectory);
            return absolutePath;
        }
    }

    /// <summary>
    /// 相対パスを絶対パスに解決する。
    /// 既に絶対パスの場合はそのまま返し、相対パスの場合はプロジェクトディレクトリからの相対パスとして解決する。
    /// 解決に失敗した場合は元のパスをそのまま返す。
    /// </summary>
    private static string ResolvePath(string path, string projectDirectory)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        if (string.IsNullOrEmpty(projectDirectory))
        {
            if (Path.IsPathRooted(path))
                return path;
            Serilog.Log.Warning("ProjectDirectory が null/空のため相対パスを解決できません: {Path}", path);
            return string.Empty;
        }

        try
        {
            string resolved;
            if (Path.IsPathRooted(path))
            {
                resolved = Path.GetFullPath(path);
            }
            else
            {
                resolved = Path.GetFullPath(Path.Combine(projectDirectory, path));
            }

            // パス正規化後の検証
            string normalizedProjectDir = Path.GetFullPath(projectDirectory);
            string baseDir = normalizedProjectDir;
            if (!baseDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                baseDir += Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(resolved, normalizedProjectDir, StringComparison.OrdinalIgnoreCase))
            {
                Serilog.Log.Warning("プロジェクトディレクトリ外のパスを拒否: {ResolvedPath}", resolved);
                return string.Empty;
            }

            return resolved;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "ResolvePath failed for {Path} in {Dir}", path, projectDirectory);
            return path;
        }
    }
}

internal sealed record ProjectData
{
    public int Version { get; init; }
    public SyncMode SyncMode { get; init; }
    public GapBehavior GapBehavior { get; init; }
    public List<TrackData> Tracks { get; init; } = [];
}

internal sealed record TrackData
{
    public Guid Id { get; init; }
    public string FilePath { get; init; } = "";
    public string Name { get; init; } = "";
    public TimeSpan MediaIn { get; init; }
    public TimeSpan? MediaOut { get; init; }
    public TimeSpan TimelineOffset { get; init; }
    public TimeSpan MediaDuration { get; init; }
    public TimeSpan SyncOffset { get; init; }
    public double? FrameRate { get; init; }
    public bool IsEnabled { get; init; }
}
