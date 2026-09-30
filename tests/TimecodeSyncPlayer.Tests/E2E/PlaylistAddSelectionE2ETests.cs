using System.IO;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// 「Playlist に追加」は行を先に出し、長さの読み取りが全部終わってから完了の処理をする。
/// その間に利用者が選んだ行を、完了の処理が再生中の行（空から足したときは先頭）で
/// 上書きしないことを確かめる（K2）。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class PlaylistAddSelectionE2ETests
{
    private const int CopyCount = 10;

    [Fact]
    public void AddFiles_SelectionMadeWhileDurationsAreRead_IsKeptAfterCompletion()
    {
        (string exe, string video) = SystemScenarioE2ETests.RequirePrerequisites();
        // 同じ素材のコピーを 10 本足して、長さの読み取りの窓を広げる。
        var copies = new List<string>();
        for (int index = 0; index < CopyCount; index++)
            copies.Add(SystemScenarioE2ETests.CreateDialogFileCopy(video, $"addsel{index:D2}"));

        try
        {
            using var app = E2EAppRunner.Start(exe, "--vo null");
            SystemScenarioE2ETests.AddPlaylistFiles(app, copies.ToArray());
            ListBox playlist = SystemScenarioE2ETests.Playlist(app);
            E2EAssert.WaitUntil(() => playlist.Items.Length >= 2, TimeSpan.FromSeconds(8));

            SystemScenarioE2ETests.SelectPlaylistItem(
                playlist, 1, Path.GetFileNameWithoutExtension(copies[1]));
            // 空から足したので、完了の処理が先頭の行を読み込むまで長さは 0 のまま。
            // ここで 0 でなければ選ぶ前に追加が終わっており、この試験は何も確かめていない。
            double durationAtSelect = SystemScenarioE2ETests.DurationSeconds(app);
            durationAtSelect.Should().Be(0,
                "the row must be selected while the add is still reading durations");

            // 追加の完了（先頭の行の読み込み）まで待つ。
            E2EAssert.WaitUntil(
                () => SystemScenarioE2ETests.DurationSeconds(app) > 0,
                TimeSpan.FromSeconds(30));
            Thread.Sleep(500);

            AutomationElement second = playlist.Items[1];
            second.Name.Should().Contain(Path.GetFileNameWithoutExtension(copies[1]));
            second.Patterns.SelectionItem.Pattern.IsSelected.Value.Should().BeTrue(
                "the add completion must not overwrite the row the user selected");
            app.Button("BtnMoveTrackUp").IsEnabled.Should().BeTrue(
                "the second row is selected, so it can move up");
        }
        finally
        {
            foreach (string copy in copies)
                SystemScenarioE2ETests.DeleteDialogFile(copy);
        }
    }
}
