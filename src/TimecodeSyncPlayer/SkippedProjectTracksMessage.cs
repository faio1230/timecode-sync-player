using System.IO;
using System.Text;

namespace TimecodeSyncPlayer;

/// <summary>
/// 0.4.6: プロジェクトを開いたときに反映できなかったトラックの一覧を、利用者向けの文面にする。
/// 以前はログに残すだけで黙って除外しており、素材の置き場所しだいでトラックが消えても気づけなかった。
/// </summary>
internal static class SkippedProjectTracksMessage
{
    /// <summary>一覧に並べる最大件数。超えたぶんは件数だけ示す（ダイアログが画面を越えないように）。</summary>
    internal const int MaxListed = 10;

    internal const string Caption = "読み込めなかったトラックがあります";

    /// <summary>反映できなかったトラックが無ければ null。</summary>
    internal static string? Format(IReadOnlyList<SkippedProjectTrack> skipped)
    {
        if (skipped.Count == 0) return null;

        var text = new StringBuilder();
        text.Append(skipped.Count).AppendLine(" 件のトラックを読み込めませんでした。").AppendLine();
        foreach (SkippedProjectTrack track in skipped.Take(MaxListed))
        {
            text.Append("・").Append(track.Name).Append("（").Append(track.Reason).AppendLine("）");
            if (!string.IsNullOrEmpty(track.Path))
                text.Append("　　").AppendLine(track.Path);
        }
        if (skipped.Count > MaxListed)
            text.Append("ほか ").Append(skipped.Count - MaxListed).AppendLine(" 件").AppendLine();
        else
            text.AppendLine();

        text.Append("このまま上書き保存すると、これらのトラックはプロジェクトから外れます。")
            .Append("素材の場所を確認してから、プロジェクトを開き直してください。");
        return text.ToString();
    }
}
