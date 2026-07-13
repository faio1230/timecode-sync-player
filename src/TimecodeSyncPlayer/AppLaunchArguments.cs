namespace TimecodeSyncPlayer;

public sealed record AppLaunchArguments(
    string? OpenPath,
    IReadOnlyList<string> PlaylistPaths,
    string? LoadProjectPath,
    string? SaveProjectPath)
{
    public IReadOnlyList<string> InitialPlaylistPaths
    {
        get
        {
            if (PlaylistPaths.Count == 0)
                return OpenPath != null ? [OpenPath] : [];

            return OpenPath != null
                ? new[] { OpenPath }.Concat(PlaylistPaths).ToArray()
                : PlaylistPaths;
        }
    }

    public static AppLaunchArguments Parse(IReadOnlyList<string> args)
    {
        string? openPath = null;
        var playlistPaths = new List<string>();
        string? loadProjectPath = null;
        string? saveProjectPath = null;

        for (int i = 1; i < args.Count; i++)
        {
            if (args[i] == "--open" && i + 1 < args.Count)
            {
                openPath = args[++i];
                continue;
            }

            if (args[i] == "--playlist")
            {
                while (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    playlistPaths.Add(args[++i]);
                continue;
            }

            if (args[i].StartsWith("--load-project=", StringComparison.Ordinal))
            {
                loadProjectPath = args[i].Substring("--load-project=".Length);
                continue;
            }

            if (args[i] == "--load-project" && i + 1 < args.Count)
            {
                loadProjectPath = args[++i];
                continue;
            }

            if (args[i].StartsWith("--save-project=", StringComparison.Ordinal))
            {
                saveProjectPath = args[i].Substring("--save-project=".Length);
                continue;
            }

            if (args[i] == "--save-project" && i + 1 < args.Count)
            {
                saveProjectPath = args[++i];
            }
        }

        return new AppLaunchArguments(openPath, playlistPaths, loadProjectPath, saveProjectPath);
    }
}
