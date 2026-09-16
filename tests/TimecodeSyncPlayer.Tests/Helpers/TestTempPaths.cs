using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// Q2: テストの一時ディレクトリは作業ツリーごとに分ける。
/// 複数の作業ツリーが同じ %TEMP%\TimecodeSyncPlayer.Tests を共有すると、
/// test_clip.mp4 のような安定パスへ同時に書き込み・読み出しが起きて
/// 「別のプロセスが使用中」で落ちる（E2E と非 E2E を並行実行した場合）。
/// リポジトリルートのハッシュを作業ツリーの識別子に使い、ツリー内では
/// 従来どおり同じパスを返す（TestVideoFactory の「安定した同じパス」契約は維持）。
/// </summary>
internal static class TestTempPaths
{
    private static readonly Lazy<string> LazyRoot = new(CreateRoot);

    /// <summary>この作業ツリー専用のテスト一時ルート。</summary>
    public static string Root => LazyRoot.Value;

    public static string Combine(params string[] parts)
    {
        string[] all = new string[parts.Length + 1];
        all[0] = Root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    private static string CreateRoot()
    {
        string repoRoot = FindRepositoryRoot();
        string normalized = repoRoot.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string token = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", token);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TimecodeSyncPlayer.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
