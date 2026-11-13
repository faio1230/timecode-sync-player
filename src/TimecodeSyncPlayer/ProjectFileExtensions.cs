using System.IO;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.7: プロジェクトのファイルの拡張子。保存の既定は .erytheia、v0.6.6 までの .tsp も開ける
/// （中身の形式は同じ。拡張子で読み方は変えない）。
/// </summary>
internal static class ProjectFileExtensions
{
    public const string Extension = ".erytheia";
    public const string LegacyExtension = ".tsp";

    /// <summary>保存と読み込みの窓の型（.tsp も選べる）。</summary>
    public const string DialogFilter = "ERYTHEIA プロジェクト (*.erytheia;*.tsp)|*.erytheia;*.tsp";

    /// <summary>保存の窓で拡張子を書かなかったときに足す拡張子（先頭の点なし）。</summary>
    public const string DialogDefaultExtension = "erytheia";

    /// <summary>プロジェクトのファイル（.erytheia か .tsp）か。</summary>
    public static bool IsProjectFile(string? path) =>
        path != null &&
        (string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Path.GetExtension(path), LegacyExtension, StringComparison.OrdinalIgnoreCase));
}
