using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.6 R-2・R-6・R-7 と窓の幅: ボタンの見せ方を決める純粋関数（ON の判定・ツールチップの文字・再生の行の幅）と、
/// MainWindow.xaml の配線（AutomationId・文字は変えない）。
/// </summary>
public class ButtonLookTests
{
    // ---- R-7: ON/OFF のボタンの ON の判定 ----

    [Theory]
    [InlineData("Sync ON", true)]
    [InlineData("Spout ON", true)]
    [InlineData("Timeline ON", true)]
    [InlineData("Card: ON", true)]
    [InlineData("MUTE ON", true)]
    [InlineData("ON", true)]
    [InlineData("Sync OFF", false)]
    [InlineData("Spout OFF", false)]
    [InlineData("Timeline OFF", false)]
    [InlineData("Card: OFF", false)]
    [InlineData("MUTE OFF", false)]
    [InlineData("Spout N/A", false)]
    [InlineData("BUTTON", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOnLabel_IsTrueOnlyForAnOnWordAtTheEnd(string? label, bool expected)
    {
        ButtonLook.IsOnLabel(label).Should().Be(expected);
    }

    [Fact]
    public void IsOnLabel_AgreesWithTheExistingToggleLabels()
    {
        // 実際のボタンの文字の作り（ToggleLabelFormatter と各 ViewModel）と合わせて確かめる。
        ButtonLook.IsOnLabel(ToggleLabelFormatter.Format(true, "Spout ON", "Spout OFF")).Should().BeTrue();
        ButtonLook.IsOnLabel(ToggleLabelFormatter.Format(false, "Spout ON", "Spout OFF")).Should().BeFalse();
        ButtonLook.IsOnLabel(SpoutStartupState.FromInitializationResult(false).ToggleLabel).Should().BeFalse();

        var output = new ViewModels.OutputControlViewModel();
        ButtonLook.IsOnLabel(output.TestCardToggleLabel).Should().BeFalse();
        output.ToggleTestCard();
        ButtonLook.IsOnLabel(output.TestCardToggleLabel).Should().BeTrue();
    }

    [Fact]
    public void OnLabelConverter_ConvertsLabelToBool()
    {
        var converter = new OnLabelConverter();
        converter.Convert("Sync ON", typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture).Should().Be(true);
        converter.Convert("Sync OFF", typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture).Should().Be(false);
        converter.Convert(null, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture).Should().Be(false);
    }

    // ---- R-6: ツールチップと押せない理由 ----

    [Fact]
    public void Compose_AddsTheReasonOnlyWhenThereIsOne()
    {
        ButtonToolTipText.Compose("説明", null).Should().Be("説明");
        ButtonToolTipText.Compose("説明", "  ").Should().Be("説明");
        ButtonToolTipText.Compose("説明", "理由").Should().Be("説明\n押せない理由: 理由");
    }

    [Fact]
    public void ApplyCanvas_ShowsTheCanvasGateReason()
    {
        string description = ButtonToolTipText.Describe(ButtonToolTipKey.ApplyCanvas);
        string? playing = CanvasChangeGate.DescribeReason(isPlaying: true, isLtcFollowing: false, isRenderingFrozenOnly: false);

        ButtonToolTipText.Compose(description, playing)
            .Should().Be(description + "\n押せない理由: 再生中はキャンバスを変更できません。");
        ButtonToolTipText.Compose(description,
                CanvasChangeGate.DescribeReason(isPlaying: false, isLtcFollowing: false, isRenderingFrozenOnly: false))
            .Should().Be(description, "変更できるときは理由を出さない");
    }

    [Fact]
    public void EveryKey_HasADescription()
    {
        foreach (ButtonToolTipKey key in Enum.GetValues<ButtonToolTipKey>())
            ButtonToolTipText.Describe(key).Should().NotBeNullOrWhiteSpace(key.ToString());
    }

    [Theory]
    [InlineData(nameof(ButtonToolTipKey.StartLtc), "LTC を受信中です。止めるときは STOP を押してください")]
    [InlineData(nameof(ButtonToolTipKey.StopLtc), "LTC は止まっています。START で受信を始めます")]
    [InlineData(nameof(ButtonToolTipKey.RemoveFromPlaylist), "トラックを選んでください")]
    [InlineData(nameof(ButtonToolTipKey.Fullscreen), "出力先のディスプレイが見つかりません")]
    [InlineData(nameof(ButtonToolTipKey.Spout), "映像出力の準備ができていないため使えません")]
    public void For_DisabledButton_ShowsDescriptionAndReason(string keyName, string reason)
    {
        ButtonToolTipKey key = Enum.Parse<ButtonToolTipKey>(keyName);
        ButtonToolTipText.For(key, isEnabled: false)
            .Should().Be(ButtonToolTipText.Describe(key) + "\n押せない理由: " + reason);
        ButtonToolTipText.For(key, isEnabled: true)
            .Should().Be(ButtonToolTipText.Describe(key), "押せるときは説明だけ");
    }

    [Fact]
    public void For_ButtonWithoutAKnownReason_ShowsOnlyTheDescription()
    {
        ButtonToolTipText.For(ButtonToolTipKey.PlayPause, isEnabled: false)
            .Should().Be(ButtonToolTipText.Describe(ButtonToolTipKey.PlayPause));
    }

    [Fact]
    public void ToolTipConverter_UsesTheKeyAndIsEnabled()
    {
        var converter = new ButtonToolTipConverter();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        converter.Convert(false, typeof(object), "StopLtc", culture)
            .Should().Be(ButtonToolTipText.For(ButtonToolTipKey.StopLtc, isEnabled: false));
        converter.Convert(true, typeof(object), "StopLtc", culture)
            .Should().Be(ButtonToolTipText.Describe(ButtonToolTipKey.StopLtc));
        converter.Convert(true, typeof(object), "NoSuchKey", culture).Should().BeNull();
    }

    // ---- 窓の幅: 再生の並びを切らない ----

    [Fact]
    public void RightGroupMaxWidth_IsWhatIsLeftAfterLeftAndCenter()
    {
        ControlRowLayout.RightGroupMaxWidth(800, 108, 324, 140).Should().Be(368);
    }

    [Fact]
    public void RightGroupMaxWidth_NeverGoesBelowTheWidestPart()
    {
        ControlRowLayout.RightGroupMaxWidth(400, 108, 324, 140).Should().Be(140);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void RightGroupMaxWidth_IsUnlimitedBeforeLayout(double rowWidth)
    {
        ControlRowLayout.RightGroupMaxWidth(rowWidth, 108, 324, 140).Should().Be(double.PositiveInfinity);
    }

    // ---- MainWindow.xaml の配線 ----

    [Fact]
    public void MainWindow_KeepsAutomationIdsAndWiresTheLook()
    {
        string xaml = File.ReadAllText(Path.Combine(SourceRoot(), "MainWindow.xaml"));

        // 既存の E2E が探す AutomationId は残す
        foreach (string id in new[]
                 {
                     "BtnOpen", "BtnFullscreen", "BtnPreviousTrack", "BtnNextTrack", "BtnBack", "BtnFrameBack", "BtnPlay",
                     "BtnFrameFwd", "BtnFwd", "BtnMute", "BtnSpeed", "BtnSpout", "BtnTestCard", "BtnTimeline",
                     "BtnRefreshLtcDevices", "BtnStartLtc", "BtnStopLtc", "BtnToggleSync", "BtnApplyCanvas", "BtnGpuRetry",
                     "BtnAddToPlaylist", "BtnRemoveFromPlaylist", "BtnClearPlaylist", "BtnMoveTrackUp", "BtnMoveTrackDown",
                     "BtnSaveProject", "BtnLoadProject",
                 })
        {
            xaml.Should().Contain($"AutomationProperties.AutomationId=\"{id}\"");
        }

        // 無効でもツールチップを出す・テンプレートを持つ
        xaml.Should().Contain("<Setter Property=\"ToolTipService.ShowOnDisabled\" Value=\"True\"/>");
        xaml.Should().Contain("<ControlTemplate TargetType=\"Button\">");

        // ConverterParameter はすべて実在のキー
        foreach (Match m in Regex.Matches(xaml, "ConverterParameter=(\\w+)"))
            Enum.TryParse(m.Groups[1].Value, out ButtonToolTipKey _).Should().BeTrue(m.Groups[1].Value);

        // ON/OFF のボタン 6 つ（Sync・Spout・Timeline・Card・MUTE・LTC の START）に ON の見せ方
        Regex.Matches(xaml, "local:ButtonLook.IsOn=").Count.Should().Be(6);
    }

    private static string SourceRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TimecodeSyncPlayer"));
}
