using System.IO;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// v0.6.6 R-12: プレイリストの行のつかむ印とツールチップ。見せ方だけを足したので、
/// ドラッグの並べ替えとオフセットの入力欄のクリックが今までどおり動くことも確かめる。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class PlaylistRowLookE2ETests
{
    private readonly ITestOutputHelper _output;

    public PlaylistRowLookE2ETests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void HoveringTheGrip_ShowsTheReorderToolTip()
    {
        (string exe, string video) = SystemScenarioE2ETests.RequirePrerequisites();
        List<string> copies = CreateCopies(video, "r12tip");
        try
        {
            using var app = E2EAppRunner.Start(exe, "--vo null");
            ListBox playlist = AddAndWait(app, copies);

            AutomationElement grip = Grip(playlist, 0);
            grip.HelpText.Should().Be(PlaylistRowLook.RowToolTip, "the grip carries the reorder tooltip");
            AutomationElement row = playlist.Items[0];
            row.HelpText.Should().Be(PlaylistRowLook.RowToolTip, "the row carries the reorder tooltip");

            // 印の上にマウスを置くと、ツールチップの窓に同じ文字が出る
            app.MainWindow.SetForeground();
            var rect = grip.BoundingRectangle;
            var center = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            Mouse.Position = new System.Drawing.Point(center.X + 30, center.Y + 30);
            Thread.Sleep(100);
            Mouse.Position = center;
            Thread.Sleep(50);
            Mouse.Position = new System.Drawing.Point(center.X + 1, center.Y);

            string? shown = null;
            E2EAssert.WaitUntil(() =>
            {
                shown = FindToolTipText(app);
                return shown != null;
            }, TimeSpan.FromSeconds(5));
            _output.WriteLine($"tooltip: {shown}");
            shown.Should().Be(PlaylistRowLook.RowToolTip);
        }
        finally
        {
            foreach (string copy in copies)
                SystemScenarioE2ETests.DeleteDialogFile(copy);
        }
    }

    [Fact]
    public void DraggingTheLastRowByTheGripToTheTop_ReordersAndOffsetBoxStillTakesClicks()
    {
        (string exe, string video) = SystemScenarioE2ETests.RequirePrerequisites();
        List<string> copies = CreateCopies(video, "r12drag");
        string[] names = copies.Select(Path.GetFileNameWithoutExtension).ToArray()!;
        try
        {
            using var app = E2EAppRunner.Start(exe, "--vo null");
            ListBox playlist = AddAndWait(app, copies);
            app.MainWindow.SetForeground();

            // OLE のドラッグは Escape が押されたままだと始まった直後に取り消される（FlaUI の Keyboard.Press は押すだけで
            // 離さないので、ほかの試験が残すことがある）。前提として確かめ、落ちたときに理由が分かるようにする。
            (GetAsyncKeyState(VkEscape) & 0x8000).Should().Be(0, "Escape must not be held down, or OLE cancels the drag at once");

            var from = Grip(playlist, 2).BoundingRectangle;
            var to = playlist.Items[0].BoundingRectangle;
            Drag(
                new System.Drawing.Point(from.Left + from.Width / 2, from.Top + from.Height / 2),
                new System.Drawing.Point(to.Left + to.Width / 2, to.Top + to.Height / 4));

            E2EAssert.WaitUntil(
                () => playlist.Items.Length == 3 && playlist.Items[0].Name.Contains(names[2], StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            playlist.Items[1].Name.Should().Contain(names[0]);
            playlist.Items[2].Name.Should().Contain(names[1]);

            // オフセットの入力欄はクリックで入力できる（印を足しても欄の位置の判定は変わらない）
            TextBox offset = playlist.Items[1]
                .FindFirstDescendant(cf => cf.ByAutomationId("TimelineOffsetTextBox"))!
                .AsTextBox();
            offset.Click();
            E2EAssert.WaitUntil(() => offset.Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(3));
            playlist.Items.Length.Should().Be(3);
            playlist.Items[0].Name.Should().Contain(names[2], "clicking the offset box must not start a drag");
        }
        finally
        {
            foreach (string copy in copies)
                SystemScenarioE2ETests.DeleteDialogFile(copy);
        }
    }

    private static List<string> CreateCopies(string video, string prefix) =>
        new[] { "a", "b", "c" }
            .Select(s => SystemScenarioE2ETests.CreateDialogFileCopy(video, prefix + s))
            .ToList();

    private static ListBox AddAndWait(E2EAppRunner app, List<string> copies)
    {
        // 3 行が全部見えるように最大化する（既定の窓の高さでは 3 行目が一覧の外に出て、UIA から見えないことがある）
        app.MainWindow.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        Thread.Sleep(500);
        SystemScenarioE2ETests.AddPlaylistFiles(app, copies.ToArray());
        ListBox playlist = SystemScenarioE2ETests.Playlist(app);
        E2EAssert.WaitUntil(() => playlist.Items.Length == 3, TimeSpan.FromSeconds(8));
        // 追加の完了（先頭の行の読み込み）まで待つ。完了の前に操作すると行が作り直されることがある。
        E2EAssert.WaitUntil(() => SystemScenarioE2ETests.DurationSeconds(app) > 0, TimeSpan.FromSeconds(30));
        Thread.Sleep(500);
        return playlist;
    }

    private static AutomationElement Grip(ListBox playlist, int index) =>
        playlist.Items[index].FindFirstDescendant(cf => cf.ByAutomationId("PlaylistDragGrip"))
        ?? throw new InvalidOperationException($"row {index} has no PlaylistDragGrip");

    /// <summary>
    /// 出ているツールチップの文字。WPF のツールチップは主窓とは別の最上位の窓（Popup）で、UIA のデスクトップの子には
    /// 出てこないことがあるので、アプリのプロセスの見えている最上位の窓（主窓以外）を Win32 で探し、その中の文字を読む。
    /// </summary>
    private static string? FindToolTipText(E2EAppRunner app)
    {
        IntPtr main = app.MainWindow.Properties.NativeWindowHandle.Value;
        foreach (IntPtr handle in VisibleTopLevelWindows(app.Process.Id))
        {
            if (handle == main)
                continue;
            AutomationElement root = app.MainWindow.Automation.FromHandle(handle);
            var texts = new List<string>();
            if (!string.IsNullOrEmpty(root.Name))
                texts.Add(root.Name);
            foreach (AutomationElement child in root.FindAllDescendants())
            {
                string name = child.Properties.Name.ValueOrDefault ?? "";
                if (!string.IsNullOrEmpty(name))
                    texts.Add(name);
            }
            string? match = texts.FirstOrDefault(t => t.Contains("ドラッグ", StringComparison.Ordinal));
            if (match != null)
                return match;
        }
        return null;
    }

    private static List<IntPtr> VisibleTopLevelWindows(int processId)
    {
        var result = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && GetWindowThreadProcessId(handle, out uint pid) != 0 && pid == (uint)processId)
                result.Add(handle);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private const int VkEscape = 0x1B;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    private static void Drag(System.Drawing.Point from, System.Drawing.Point to)
    {
        bool down = false;
        try
        {
            Mouse.Position = from;
            Thread.Sleep(80);
            Mouse.Down(MouseButton.Left);
            down = true;
            Thread.Sleep(250);
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                Mouse.Position = new System.Drawing.Point(
                    from.X + (to.X - from.X) * i / steps,
                    from.Y + (to.Y - from.Y) * i / steps);
                Thread.Sleep(40);
            }
            Thread.Sleep(300);
        }
        finally
        {
            if (down)
            {
                try { Mouse.Up(MouseButton.Left); } catch { /* finally では握り潰す */ }
                Thread.Sleep(300);
            }
        }
    }
}
