using System.IO;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// 「Playlist に追加」は行を先に出し、長さの読み取りが全部終わってから完了の処理をする。
/// その間に利用者が選んだ行を、完了の処理が再生中の行（空から足したときは先頭）で
/// 上書きしないことを確かめる（K2）。
/// v0.6.6 F-7: 長さの読み取りが 1 本数 ms になり、読み取りの所要で窓を作れなくなった。
/// 試験は操作の順を固定する: 行が増えたらすぐ 2 行目を選び、その後で追加の完了（先頭の行の読み込み）を待って、
/// 選んだ行が残っていることを確かめる。選んだ時点で完了の前だったかは出力に記録するだけにする
/// （完了の判断そのものは単体の PlaylistAddSelectionPolicyTests が固定する）。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class PlaylistAddSelectionE2ETests
{
    private const int CopyCount = 10;

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PlaylistAddSelectionE2ETests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

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
            // 0 なら選んだのは完了の前（K2 の競合を踏んだ）、0 でなければ完了の後。どちらも記録だけにする。
            double durationAtSelect = SystemScenarioE2ETests.DurationSeconds(app);
            _output.WriteLine(durationAtSelect == 0
                ? "selection was made before the add completed (the K2 race was exercised)"
                : $"selection was made after the add completed (durationAtSelect={durationAtSelect:F3})");

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
