using System;
using System.IO;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class ProjectFileFactory
{
    public static string CreateTempProjectPath()
        => Path.Combine(Path.GetTempPath(), $"tsp-test-project-{Guid.NewGuid():N}.tsp.json");

    public static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* テストクリーンアップでは例外を握り潰す */ }
    }
}
