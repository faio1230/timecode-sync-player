using FluentAssertions;
using FlaUI.Core.AutomationElements;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class TimecodeSyncPlayerE2ETests : IClassFixture<TimecodeSyncPlayerFixture>
{
    private readonly TimecodeSyncPlayerFixture _fx;
    private readonly ITestOutputHelper _out;  // デバッグ出力用

    public TimecodeSyncPlayerE2ETests(TimecodeSyncPlayerFixture fx, ITestOutputHelper output)
    {
        _fx  = fx;
        _out = output;
    }

    // ── ヘルパー ──────────────────────────────────────────────────

    /// <summary>前提条件が揃っていない場合はテストをスキップする。</summary>
    private void SkipIfNeeded()
    {
        if (_fx.Skipped)
            throw new SkipException(_fx.SkipReason ?? "前提条件不足");
    }

    private Window Win => _fx.MainWindow!;

    private Button Btn(string id)      => Win.FindFirstDescendant(cf => cf.ByAutomationId(id)).AsButton();
    private string BtnLabel(string id) => Btn(id).Name;
    private Slider SeekBar()           => Win.FindFirstDescendant(cf => cf.ByAutomationId("SeekBar")).AsSlider();
    private AutomationElement? Element(string id) => Win.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private Window? FullscreenWindow()
    {
        AutomationElement? element = Win.Automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByAutomationId("FullscreenOutputWindow"));
        return element?.AsWindow();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
    }

    private async Task PauseIfPlaying()
    {
        if (BtnLabel("BtnPlay") != "⏸")
            return;

        Btn("BtnPlay").Invoke();
        await WaitUntil(() => BtnLabel("BtnPlay") == "▶", TimeSpan.FromSeconds(2));
    }

    private string TimeLabelText()
    {
        var el = Win.FindFirstDescendant(cf => cf.ByAutomationId("TimeLabel"));
        if (el == null) return "";
        // Use TextPattern for live text reading (avoids UIA Name caching quirks)
        var tp = el.Patterns.Text.PatternOrDefault;
        if (tp != null)
        {
            string t = tp.DocumentRange.GetText(-1).Trim();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return el.Name.Trim();
    }

    private string MetaLineText()
    {
        var el = Win.FindFirstDescendant(cf => cf.ByAutomationId("MetaLineText"));
        if (el == null) return "";
        var tp = el.Patterns.Text.PatternOrDefault;
        if (tp != null)
        {
            string t = tp.DocumentRange.GetText(-1).Trim();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return el.Name.Trim();
    }

    private string CurrentTrackLabelText()
    {
        var el = Win.FindFirstDescendant(cf => cf.ByAutomationId("CurrentTrackLabel"));
        if (el == null) return "";
        var tp = el.Patterns.Text.PatternOrDefault;
        if (tp != null)
        {
            string t = tp.DocumentRange.GetText(-1).Trim();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return el.Name.Trim();
    }

    /// <summary>TimeLabel から現在位置の秒数を取得する（H:MM:SS:FF 形式の先頭部分）。</summary>
    private static double ParsePositionSeconds(string timeLabelText)
    {
        // 例: "0:00:03:12 / 0:00:10:00"
        string current = timeLabelText.Split('/')[0].Trim();
        string[] parts = current.Split(':');
        if (parts.Length < 3) return 0;
        return int.Parse(parts[0]) * 3600
             + int.Parse(parts[1]) * 60
             + int.Parse(parts[2]);
    }

    // ── テスト 1: 起動確認 ─────────────────────────────────────────

    [SkippableFact]
    public void AppLaunches_WindowIsVisible()
    {
        SkipIfNeeded();

        Win.Should().NotBeNull();
        Win.Title.Should().Be(ApplicationVersion.WindowTitle);
        E2EAssert.WaitUntil(
            () => E2EWindowVisibility.IsVisible(Win),
            TimeSpan.FromSeconds(3));
    }

    // ── フルスクリーン出力 ────────────────────────────────────────

    [Fact]
    public async Task Fullscreen_ToggleOpensAndClosesWindow()
    {
        SkipIfNeeded();

        Btn("BtnFullscreen").Invoke();
        await WaitUntil(() => FullscreenWindow() != null, TimeSpan.FromSeconds(5));

        Window? fullscreen = FullscreenWindow();
        fullscreen.Should().NotBeNull();
        BtnLabel("BtnFullscreen").Should().Be("EXIT FULLSCREEN");

        // ESCキー物理入力はRDPセッションで合成不可のためユニットテストで担保する。
        Btn("BtnFullscreen").Invoke();
        await WaitUntil(() => FullscreenWindow() == null, TimeSpan.FromSeconds(5));

        FullscreenWindow().Should().BeNull();
        BtnLabel("BtnFullscreen").Should().Be("FULLSCREEN");
    }

    // ── テスト 2: 動画ロード確認 ────────────────────────────────────

    [SkippableFact]
    public void LoadVideo_TimeLabelUpdates()
    {
        SkipIfNeeded();

        // Fixture が --open で起動して 5000ms 待機済み
        string text = TimeLabelText();
        text.Should().NotBe("0:00 / 0:00",
            because: "動画ロード後は H:MM:SS:FF 形式で更新されるはず");
        text.Should().Contain("/");
    }

    [SkippableFact]
    public async Task LoadVideo_MetaLineIsDisplayed()
    {
        SkipIfNeeded();

        await WaitUntil(
            () => Element("MetaLineText") != null && !string.IsNullOrWhiteSpace(MetaLineText()),
            TimeSpan.FromSeconds(3));

        Element("MetaLineText").Should().NotBeNull();
        MetaLineText().Should().NotBeNullOrWhiteSpace(
            because: "動画ロード後は解像度・fps・コーデックのメタデータが表示されるはず");
    }

    // ── テスト 3: Playlist UI ─────────────────────────────────────

    [SkippableFact]
    public void PlaylistControls_AreVisible()
    {
        SkipIfNeeded();

        Element("LtcDeviceCombo").Should().NotBeNull();
        Element("BtnRefreshLtcDevices").Should().NotBeNull();
        Element("BtnStartLtc").Should().NotBeNull();
        Element("BtnStopLtc").Should().NotBeNull();
        Element("BtnToggleSync").Should().NotBeNull();
        Element("LtcFpsModeCombo").Should().NotBeNull();
        Element("LtcTimecodeText").Should().NotBeNull();
        Element("LtcRealTimeText").Should().NotBeNull();
        Element("LtcFormatText").Should().NotBeNull();
        Element("PlaylistList").Should().NotBeNull();
        Element("BtnAddToPlaylist").Should().NotBeNull();
        Element("BtnRemoveFromPlaylist").Should().NotBeNull();
        Element("BtnMoveTrackUp").Should().NotBeNull();
        Element("BtnMoveTrackDown").Should().NotBeNull();
        Element("BtnClearPlaylist").Should().NotBeNull();
        Element("BtnPreviousTrack").Should().NotBeNull();
        Element("BtnNextTrack").Should().NotBeNull();
        Element("CurrentTrackLabel").Should().NotBeNull();
    }

    [SkippableFact]
    public async Task SyncToggle_ChangesButtonLabel()
    {
        SkipIfNeeded();

        string before = BtnLabel("BtnToggleSync");
        Btn("BtnToggleSync").Invoke();
        await Task.Delay(300);
        string after = BtnLabel("BtnToggleSync");

        after.Should().NotBe(before);

        Btn("BtnToggleSync").Invoke();
        await Task.Delay(300);
    }

    [SkippableFact]
    public async Task PlaylistNextPrevious_UpdatesCurrentTrackLabel()
    {
        SkipIfNeeded();

        string before = CurrentTrackLabelText();
        string firstCommand = before.StartsWith("2/2", StringComparison.Ordinal)
            ? "BtnPreviousTrack"
            : "BtnNextTrack";
        string secondCommand = firstCommand == "BtnNextTrack"
            ? "BtnPreviousTrack"
            : "BtnNextTrack";
        string expectedAfterFirst = firstCommand == "BtnNextTrack" ? "2/2" : "1/2";
        string expectedAfterSecond = secondCommand == "BtnNextTrack" ? "2/2" : "1/2";

        Btn(firstCommand).Invoke();
        await PauseIfPlaying();
        await WaitUntil(() => CurrentTrackLabelText().StartsWith(expectedAfterFirst, StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        string afterFirst = CurrentTrackLabelText();

        afterFirst.Should().NotBe(before);
        afterFirst.Should().StartWith(expectedAfterFirst);

        Btn(secondCommand).Invoke();
        await PauseIfPlaying();
        await WaitUntil(() => CurrentTrackLabelText().StartsWith(expectedAfterSecond, StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        string afterSecond = CurrentTrackLabelText();

        afterSecond.Should().StartWith(expectedAfterSecond);
    }

    [SkippableFact]
    public void PlaylistMoveButtons_AreAvailable()
    {
        SkipIfNeeded();

        Element("BtnMoveTrackUp").Should().NotBeNull();
        Element("BtnMoveTrackDown").Should().NotBeNull();
        CurrentTrackLabelText().Should().NotBeNullOrWhiteSpace();
    }

    // ── テスト 5: 再生/停止トグル ────────────────────────────────────

    [SkippableFact]
    public async Task PlayPause_TogglesButtonLabel()
    {
        SkipIfNeeded();
        await PauseIfPlaying();

        Btn("BtnPlay").Invoke();
        await WaitUntil(() => BtnLabel("BtnPlay") == "⏸", TimeSpan.FromSeconds(2));
        BtnLabel("BtnPlay").Should().Be("⏸", because: "再生ボタンで再生状態へ切り替わるはず");

        // 元の状態に戻す
        Btn("BtnPlay").Invoke();
        await WaitUntil(() => BtnLabel("BtnPlay") == "▶", TimeSpan.FromSeconds(2));
        BtnLabel("BtnPlay").Should().Be("▶", because: "再生ボタンで一時停止状態へ戻るはず");
    }

    // ── テスト 6: +10秒シーク ─────────────────────────────────────

    [SkippableFact]
    public async Task SeekForward_TimeAdvances()
    {
        SkipIfNeeded();

        // 一時停止してから現在位置を記録
        string pauseLabel = BtnLabel("BtnPlay");
        bool wasPlaying   = pauseLabel == "⏸";
        if (wasPlaying) { Btn("BtnPlay").Invoke(); await Task.Delay(300); }

        Btn("BtnBack").Invoke();
        await Task.Delay(500);

        double before = ParsePositionSeconds(TimeLabelText());

        Btn("BtnFwd").Invoke();
        await Task.Delay(500);

        double after = ParsePositionSeconds(TimeLabelText());
        after.Should().BeGreaterThan(before,
            because: "+10秒ボタンで時間が進むはず");

        // 元の再生状態に戻す
        if (wasPlaying) { Btn("BtnPlay").Invoke(); await Task.Delay(300); }
    }

    // ── テスト 7: -10秒シーク ─────────────────────────────────────

    [SkippableFact]
    public async Task SeekBack_TimeDecreases()
    {
        SkipIfNeeded();

        // まず +10s してから -10s してタイムが減ることを確認
        string pauseLabel = BtnLabel("BtnPlay");
        bool wasPlaying   = pauseLabel == "⏸";
        if (wasPlaying) { Btn("BtnPlay").Invoke(); await Task.Delay(300); }

        Btn("BtnFwd").Invoke();
        await Task.Delay(500);
        double before = ParsePositionSeconds(TimeLabelText());

        Btn("BtnBack").Invoke();
        await Task.Delay(500);
        double after = ParsePositionSeconds(TimeLabelText());

        after.Should().BeLessThan(before,
            because: "-10秒ボタンで時間が戻るはず");

        if (wasPlaying) { Btn("BtnPlay").Invoke(); await Task.Delay(300); }
    }

    // ── テスト 8: シークバークリック ───────────────────────────────

    [SkippableFact]
    public async Task SeekBarClick_JumpsToClickedPositionAndDoesNotRevert()
    {
        SkipIfNeeded();

        string pauseLabel = BtnLabel("BtnPlay");
        bool wasPlaying   = pauseLabel == "⏸";
        if (wasPlaying) { Btn("BtnPlay").Invoke(); await Task.Delay(300); }

        SeekBar().Patterns.RangeValue.Pattern.SetValue(0.70);
        await Task.Delay(1800);

        double after = ParsePositionSeconds(TimeLabelText());
        after.Should().BeInRange(12.0, 16.0,
            because: "20秒動画のシークバー70%付近をクリックしたら、その位置に留まるはず");

        if (wasPlaying) { Btn("BtnPlay").Invoke(); await Task.Delay(300); }
    }

    // ── テスト 9: 2×速度 ──────────────────────────────────────────

    [SkippableFact]
    public async Task SpeedButton_DoubleSpeed_DoesNotCrash()
    {
        SkipIfNeeded();

        // 再生中にする
        string pauseLabel = BtnLabel("BtnPlay");
        if (pauseLabel == "▶") { Btn("BtnPlay").Invoke(); await Task.Delay(300); }

        // BtnSpeed を 1 回押すと 2× になる
        Btn("BtnSpeed").Invoke();
        await Task.Delay(800);

        // クラッシュしていないことと TimeLabel が更新されていることを確認
        string text = TimeLabelText();
        text.Should().Contain("/", because: "クラッシュしていなければ TimeLabel が存在するはず");

        // 速度を 1× に戻す（さらに 3 回押すと 4× → 0.5× → 1× になる）
        Btn("BtnSpeed").Invoke();
        await Task.Delay(200);
        Btn("BtnSpeed").Invoke();
        await Task.Delay(200);
        Btn("BtnSpeed").Invoke();
        await Task.Delay(300);
    }

    // ── テスト 10: Sync Mode / Gap Behavior UI ─────────────────────

    [SkippableFact]
    public void SyncModeControls_AreVisible()
    {
        SkipIfNeeded();

        Element("SyncModeCombo").Should().NotBeNull();
        Element("GapBehaviorCombo").Should().NotBeNull();
    }

    [SkippableFact]
    public async Task SyncMode_CanSwitchBetweenSingleAndContinue()
    {
        SkipIfNeeded();

        var syncModeCombo = Win.FindFirstDescendant(cf => cf.ByAutomationId("SyncModeCombo")).AsComboBox();
        syncModeCombo.Should().NotBeNull();

        // Continue モードに切替
        syncModeCombo.Select(1);
        await Task.Delay(300);

        var gapBehaviorCombo = Win.FindFirstDescendant(cf => cf.ByAutomationId("GapBehaviorCombo")).AsComboBox();
        gapBehaviorCombo.Should().NotBeNull();
        gapBehaviorCombo.IsEnabled.Should().BeTrue("Continue モードでは Gap Behavior が有効になるはず");

        // Single モードに戻す
        syncModeCombo.Select(0);
        await Task.Delay(300);

        gapBehaviorCombo.IsEnabled.Should().BeFalse("Single モードでは Gap Behavior が無効化されるはず");
    }

    [SkippableFact]
    public void GapBehavior_IsDisabledInSingleMode()
    {
        SkipIfNeeded();

        var gapBehaviorCombo = Win.FindFirstDescendant(cf => cf.ByAutomationId("GapBehaviorCombo")).AsComboBox();
        gapBehaviorCombo.Should().NotBeNull();
        gapBehaviorCombo.IsEnabled.Should().BeFalse("初期状態(Single)では Gap Behavior が無効化されているはず");
    }
}
