using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.6 R-12: プレイリストの行のつかむ印とツールチップの配線（MainWindow.xaml）。見せ方だけで、
/// ドラッグの開始・ドロップ・「上へ」「下へ」の配線は変えないことも確かめる。
/// </summary>
public class PlaylistRowLookTests
{
    [Fact]
    public void RowToolTip_SaysDragReordersAndUpperRowWinsInContinue()
    {
        PlaylistRowLook.RowToolTip.Should().Contain("ドラッグで並べ替える");
        PlaylistRowLook.RowToolTip.Should().Contain("Continue で時間が重なるときは上の行を再生する");
        PlaylistRowLook.GripText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RowToolTip_ClaimMatchesTimelineLookup()
    {
        // ツールチップの「上の行」は FindTrackAtTimelinePosition の走査の順（番号の小さい行が先）と一致する。
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\upper.mp4", "C:\\Videos\\lower.mp4"], autoOffset: false);
        state.Tracks[0] = state.Tracks[0] with { TimelineOffset = TimeSpan.Zero, MediaDuration = TimeSpan.FromSeconds(10) };
        state.Tracks[1] = state.Tracks[1] with { TimelineOffset = TimeSpan.FromSeconds(5), MediaDuration = TimeSpan.FromSeconds(10) };

        state.FindTrackAtTimelinePosition(7).Track.Should().BeSameAs(state.Tracks[0]);
    }

    [Fact]
    public void PlaylistTemplate_HasGripWithCursorAndToolTip()
    {
        string template = PlaylistItemTemplate();

        template.Should().Contain("AutomationProperties.AutomationId=\"PlaylistDragGrip\"");
        template.Should().Contain("Content=\"{x:Static local:PlaylistRowLook.GripText}\"");
        template.Should().Contain("Cursor=\"SizeAll\"");
        template.Should().Contain("ToolTip=\"{x:Static local:PlaylistRowLook.RowToolTip}\"");
        // 印は行の左端（DockPanel の Left）で、行の中身（名前・Offset・Dur/Eff）より先に置く
        int grip = template.IndexOf("x:Name=\"PlaylistDragGrip\"", StringComparison.Ordinal);
        grip.Should().BeGreaterThan(0);
        template.IndexOf("DockPanel.Dock=\"Left\"", grip, StringComparison.Ordinal).Should().BeGreaterThan(grip);
        template.IndexOf("Text=\"{Binding Name}\"", StringComparison.Ordinal).Should().BeGreaterThan(grip);
    }

    [Fact]
    public void PlaylistRows_HaveRowToolTip()
    {
        string xaml = Xaml();
        Match style = Regex.Match(xaml, "<ListBox.ItemContainerStyle>(.*?)</ListBox.ItemContainerStyle>", RegexOptions.Singleline);
        style.Success.Should().BeTrue();
        style.Groups[1].Value.Should().Contain("BasedOn=\"{StaticResource {x:Type ListBoxItem}}\"");
        style.Groups[1].Value.Should().Contain("<Setter Property=\"ToolTip\" Value=\"{x:Static local:PlaylistRowLook.RowToolTip}\"/>");
        // 行の見た目はツールチップだけを足す（ほかの Setter は足さない）
        Regex.Matches(style.Groups[1].Value, "<Setter ").Count.Should().Be(1);
    }

    [Fact]
    public void PlaylistTemplate_KeepsTwoLineOffsetAndDurEffLayout()
    {
        string template = PlaylistItemTemplate();
        int offset = template.IndexOf("Text=\"Offset:\"", StringComparison.Ordinal);
        int textBox = template.IndexOf("x:Name=\"TimelineOffsetTextBox\"", StringComparison.Ordinal);
        int dur = template.IndexOf("Text=\"Dur:\"", StringComparison.Ordinal);
        int eff = template.IndexOf("Text=\" Eff:\"", StringComparison.Ordinal);

        offset.Should().BeGreaterThan(0);
        textBox.Should().BeGreaterThan(offset);
        dur.Should().BeGreaterThan(textBox);
        eff.Should().BeGreaterThan(dur);
        // Offset の行は Dur の前で閉じる（別の行）。Dur と Eff は同じ行
        template.Substring(textBox, dur - textBox).Should().Contain("</StackPanel>");
        template.Substring(dur, eff - dur).Should().NotContain("</StackPanel>");
        // オフセットの入力欄の配線は変えない
        template.Should().Contain("LostFocus=\"TimelineOffsetTextBox_LostFocus\"");
        template.Should().Contain("KeyDown=\"TimelineOffsetTextBox_KeyDown\"");
    }

    [Fact]
    public void PlaylistList_KeepsDragDropAndMoveWiring()
    {
        string xaml = Xaml();
        foreach (string wiring in new[]
                 {
                     "AllowDrop=\"True\"",
                     "PreviewMouseLeftButtonDown=\"PlaylistList_PreviewMouseLeftButtonDown\"",
                     "PreviewMouseMove=\"PlaylistList_PreviewMouseMove\"",
                     "DragOver=\"PlaylistList_DragOver\"",
                     "Drop=\"PlaylistList_Drop\"",
                     "Command=\"{Binding Playlist.MoveUpCommand}\"",
                     "Command=\"{Binding Playlist.MoveDownCommand}\"",
                 })
        {
            xaml.Should().Contain(wiring);
        }
        // 印にはドラッグ用のハンドラを付けない（開始の判定は ListBox のまま）
        PlaylistItemTemplate().Should().NotContain("MouseLeftButtonDown=").And.NotContain("MouseMove=");
    }

    private static string PlaylistItemTemplate()
    {
        Match m = Regex.Match(Xaml(), "<ListBox.ItemTemplate>(.*?)</ListBox.ItemTemplate>", RegexOptions.Singleline);
        m.Success.Should().BeTrue();
        return m.Groups[1].Value;
    }

    private static string Xaml() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TimecodeSyncPlayer", "MainWindow.xaml"));
}
