using System.ComponentModel;
using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// v0.6.6 R-9: オフセットの数値入力。入力 → 表示（入力欄・表示の文字・つまみ）→ 設定ファイルまでを実アプリで確かめる。
/// 設定は一時の場所に切り離す（E2ESettingsIsolation）。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class SyncOffsetInputE2ETests
{
    [SkippableFact]
    public void TypeAndEnter_AppliesClampsRejectsAndPersistsAcrossRestart()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);
        string directory = CreateTemporaryDirectory();
        string settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using (var app = E2EAppRunner.Start(prereqs.ExePath, "--vo null", settingsPath))
            {
                TextBox box = InputBox(app);
                box.Text.Should().Be("0");

                // 入力して Enter で反映。表示の文字・つまみ・設定ファイルがそろう。
                // 「+」は打たない（FlaUI の文字の送りが JIS 配列で「:」に化ける。+ 付きの解釈は単体で見る）。
                KeyboardInputHelper.ReplaceTextAndEnterOrSkip(box, "250");
                WaitForApplied(app, box, settingsPath, "+250", 250);

                // 範囲の外は丸め、欄にも丸めた値を戻す。
                KeyboardInputHelper.ReplaceTextAndEnterOrSkip(box, "-5000");
                WaitForApplied(app, box, settingsPath, "-1000", -1000);

                // 数字以外・小数は反映せず、前の値に戻す。
                KeyboardInputHelper.ReplaceTextAndEnterOrSkip(box, "abc");
                E2EAssert.WaitUntil(() => box.Text == "-1000", TimeSpan.FromSeconds(3));
                KeyboardInputHelper.ReplaceTextAndEnterOrSkip(box, "12.5");
                E2EAssert.WaitUntil(() => box.Text == "-1000", TimeSpan.FromSeconds(3));
                box.Text.Should().Be("-1000");
                ReadSettings(settingsPath)!.SyncOffsetMs.Should().Be(-1000);

                // 範囲の内側の値に戻して、再起動で戻ることを確かめる。
                KeyboardInputHelper.ReplaceTextAndEnterOrSkip(box, "37");
                WaitForApplied(app, box, settingsPath, "+37", 37);

                app.ExitNormally(TimeSpan.FromSeconds(10)).Should().BeTrue("the application should exit normally");
            }

            using var restarted = E2EAppRunner.Start(prereqs.ExePath, "--vo null", settingsPath);
            E2EAssert.WaitUntil(() => InputBox(restarted).Text == "+37", TimeSpan.FromSeconds(5));
            InputBox(restarted).Text.Should().Be("+37");
            restarted.Slider("SyncOffsetSlider").Value.Should().Be(37);
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }

    [SkippableFact]
    public void EscRestores_AndSliderChangeShowsInTheBox()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);
        string directory = CreateTemporaryDirectory();
        string settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using var app = E2EAppRunner.Start(prereqs.ExePath, "--vo null", settingsPath);
            TextBox box = InputBox(app);
            KeyboardInputHelper.ReplaceTextAndEnterOrSkip(box, "100");
            WaitForApplied(app, box, settingsPath, "+100", 100);

            // Esc: 打ちかけの値を捨てて元に戻す。
            TypeWithoutCommit(box, "-300");
            box.Text.Should().Be("-300");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            E2EAssert.WaitUntil(() => box.Text == "+100", TimeSpan.FromSeconds(3));
            box.Text.Should().Be("+100");

            // つまみで変えたら欄もそろう。
            app.Slider("SyncOffsetSlider").Patterns.RangeValue.Pattern.SetValue(-20);
            WaitForApplied(app, box, settingsPath, "-20", -20);
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }

    [SkippableFact]
    public void SpaceWhileTyping_DoesNotTogglePlayback()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);
        string directory = CreateTemporaryDirectory();
        string settingsPath = Path.Combine(directory, "settings.json");
        string video = TestVideoFactory.GetOrCreate();

        try
        {
            using var app = E2EAppRunner.Start(prereqs.ExePath, $"--vo null --playlist \"{video}\"", settingsPath);
            E2EAssert.WaitUntil(() => app.Button("BtnPlay").IsEnabled, TimeSpan.FromSeconds(10));
            E2EAssert.WaitUntil(() => app.Button("BtnPlay").Name == "▶", TimeSpan.FromSeconds(5));

            // 欄が直前にフォーカスを持つ再生ボタンからキーを横取りしないことも見るため、先に再生ボタンへフォーカスを置く。
            app.Button("BtnPlay").Focus();
            TextBox box = InputBox(app);
            TypeWithoutCommit(box, "12");
            Keyboard.Type(VirtualKeyShort.SPACE);
            Thread.Sleep(500);
            app.Button("BtnPlay").Name.Should().Be("▶", "the space typed into the box must not toggle playback");
            box.Text.Should().Be("12 ");

            Keyboard.Type(VirtualKeyShort.RETURN);
            WaitForApplied(app, box, settingsPath, "+12", 12);
            Thread.Sleep(300);
            app.Button("BtnPlay").Name.Should().Be("▶");
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }

    private static TextBox InputBox(E2EAppRunner app)
        => app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("SyncOffsetInputBox")).AsTextBox();

    /// <summary>欄の中身を置き換えるだけで確定しない（Enter も欄の外へのフォーカス移動もしない）。</summary>
    private static void TypeWithoutCommit(TextBox box, string text)
    {
        try
        {
            box.Focus();
            Thread.Sleep(80);
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_A);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(40);
            Keyboard.Type(VirtualKeyShort.DELETE);
            Thread.Sleep(40);
            Keyboard.Type(text);
            Thread.Sleep(150);
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 5)
        {
            throw new SkipException("SendInput 拒否環境のためスキップ");
        }
    }

    private static void WaitForApplied(E2EAppRunner app, TextBox box, string settingsPath, string boxText, double ms)
    {
        // v0.6.6: 値の文字（SyncOffsetValueText）は外した。値は入力欄で読む
        E2EAssert.WaitUntil(() => box.Text == boxText, TimeSpan.FromSeconds(3));
        E2EAssert.WaitUntil(() => app.Slider("SyncOffsetSlider").Value == ms, TimeSpan.FromSeconds(3));
        E2EAssert.WaitUntil(() => ReadSettings(settingsPath)?.SyncOffsetMs == ms, TimeSpan.FromSeconds(5));
    }

    private static AppSettings? ReadSettings(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "TimecodeSyncPlayer.Tests",
            "sync-offset-input",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
