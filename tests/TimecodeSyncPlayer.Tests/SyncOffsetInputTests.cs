using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Tests.Gst;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.6 R-9: オフセットの数値入力。受けるのは ms の整数だけ（前後の空白・先頭の + は許す、小数は拒む）。
/// 範囲の外は SyncOffsetPolicy で丸め、入力欄にも丸めた値を戻す。数字以外は反映せず前の値に戻す。
/// </summary>
public sealed class SyncOffsetInputParseTests
{
    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("12", 12.0)]
    [InlineData("-37", -37.0)]
    [InlineData("+80", 80.0)]
    [InlineData("  250  ", 250.0)]
    [InlineData("\t-5\t", -5.0)]
    [InlineData("-0", 0.0)]
    [InlineData("007", 7.0)]
    [InlineData("5000", 5000.0)]          // 範囲の外も値は返す（丸めは反映する側）
    [InlineData("-99999", -99999.0)]
    [InlineData("１２", 12.0)]             // 全角の数字
    [InlineData("－３０", -30.0)]          // 全角の符号
    [InlineData("＋４５", 45.0)]
    [InlineData("　60　", 60.0)]           // 全角の空白
    public void TryParseInput_AcceptsIntegers(string text, double expected)
    {
        SyncOffsetPolicy.TryParseInput(text, out double ms).Should().BeTrue();
        ms.Should().Be(expected);
        double.IsNegative(ms).Should().Be(expected < 0, "-0 は +0 にそろえる");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("12.5")]                  // 小数は拒む（丸めない）
    [InlineData("12.0")]
    [InlineData(".5")]
    [InlineData("12,5")]
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("12ms")]
    [InlineData("12 ms")]
    [InlineData("1 2")]
    [InlineData("+-5")]
    [InlineData("--5")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("٣")]                     // ASCII 以外の数字（アラビア・インド数字）は拒む
    public void TryParseInput_RejectsNonIntegers(string? text)
    {
        SyncOffsetPolicy.TryParseInput(text, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseInput_HugeDigits_AreOutOfRangeNotZero()
    {
        string huge = new('9', 400);
        SyncOffsetPolicy.TryParseInput(huge, out double ms).Should().BeTrue();
        SyncOffsetPolicy.Clamp(ms).Should().Be(SyncOffsetPolicy.MaximumMilliseconds);
        SyncOffsetPolicy.TryParseInput("-" + huge, out ms).Should().BeTrue();
        SyncOffsetPolicy.Clamp(ms).Should().Be(SyncOffsetPolicy.MinimumMilliseconds);
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(80.0, "+80")]
    [InlineData(-37.5, "-37.5")]
    [InlineData(5000.0, "+1000")]
    public void FormatInput_MatchesTheDisplayText(double ms, string expected)
    {
        SyncOffsetPolicy.FormatInput(ms).Should().Be(expected);
        SyncOffsetPolicy.FormatMilliseconds(ms).Should().Be(expected + " ms");
    }
}

public sealed class SyncOffsetInputViewModelTests
{
    private sealed class NoopLtcMonitor : ILtcMonitor
    {
        public bool IsRunning => false;
        public string? DeviceName => null;
        public int SampleRate => 0;
        public void Start(string? deviceName) { }
        public void Stop() { }
        public IReadOnlyList<string> GetCaptureDeviceNames() => [];
        public event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public void Dispose() { }
    }

    [Fact]
    public void InputText_Integer_SetsOffsetAndNotifiesBothDisplays()
    {
        var vm = new SyncViewModel(new NoopLtcMonitor());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SyncOffsetInputText = " +120 ";

        vm.SyncOffsetMs.Should().Be(120.0);
        vm.SyncOffsetText.Should().Be("+120 ms");
        vm.SyncOffsetInputText.Should().Be("+120");
        changed.Should().Contain([nameof(SyncViewModel.SyncOffsetMs), nameof(SyncViewModel.SyncOffsetText),
            nameof(SyncViewModel.SyncOffsetInputText)]);
    }

    [Theory]
    [InlineData("5000", 1000.0, "+1000")]
    [InlineData("-1001", -1000.0, "-1000")]
    public void InputText_OutOfRange_IsClampedAndShownClamped(string input, double expectedMs, string expectedText)
    {
        var vm = new SyncViewModel(new NoopLtcMonitor());

        vm.SyncOffsetInputText = input;

        vm.SyncOffsetMs.Should().Be(expectedMs);
        vm.SyncOffsetInputText.Should().Be(expectedText);
    }

    [Fact]
    public void InputText_OutOfRangeAtTheLimitAlready_StillRefreshesTheInput()
    {
        var vm = new SyncViewModel(new NoopLtcMonitor()) { SyncOffsetMs = 1000 };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SyncOffsetInputText = "3000";

        vm.SyncOffsetMs.Should().Be(1000.0);
        changed.Should().Contain(nameof(SyncViewModel.SyncOffsetInputText),
            "the box must be told to show +1000 instead of 3000");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("")]
    public void InputText_Rejected_KeepsThePreviousValueAndRefreshesTheInput(string input)
    {
        var vm = new SyncViewModel(new NoopLtcMonitor()) { SyncOffsetMs = 40 };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SyncOffsetInputText = input;

        vm.SyncOffsetMs.Should().Be(40.0);
        vm.SyncOffsetInputText.Should().Be("+40");
        changed.Should().NotContain(nameof(SyncViewModel.SyncOffsetMs), "nothing is saved");
        changed.Should().Contain(nameof(SyncViewModel.SyncOffsetInputText));
    }

    [Fact]
    public void SliderValue_UpdatesTheInputText()
    {
        var vm = new SyncViewModel(new NoopLtcMonitor());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SyncOffsetMs = -15;

        vm.SyncOffsetInputText.Should().Be("-15");
        changed.Should().Contain(nameof(SyncViewModel.SyncOffsetInputText));
    }
}

/// <summary>
/// R-9: 窓の入力欄（SyncOffsetInputBox）。Enter・欄を離れる・Esc の扱いと、つまみ・表示の文字とのそろいを、
/// 窓のハンドラを直接呼んで確かめる。設定は一時の場所に切り離す。
/// </summary>
[Collection("WpfWindow")]
public sealed class MainWindowSyncOffsetInputTests
{
    [Fact]
    public Task EnterEscAndLostFocus_ApplyRestoreAndStayInStepWithTheSlider() => OnUi(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", "r9-input", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        services.AddSingleton(new AppSettingsManager(Path.Combine(directory, "settings.json")));
        services.AddSingleton<IGstNativeApi>(new FakeGstNative { PlayerCreateResult = new IntPtr(1) });
        services.AddSingleton<IPlaybackApi>(new FakePlaybackApi());
        var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        var source = new HwndSource(new HwndSourceParameters("r9-test"));
        try
        {
            var vm = ((MainViewModel)window.DataContext).Sync;
            var box = (TextBox)window.FindName("SyncOffsetInputBox");
            var slider = (Slider)window.FindName("SyncOffsetSlider");
            box.Text.Should().Be("0");

            // Enter で反映。つまみと表示の文字もそろう。
            box.Text = " +250 ";
            Key(window, box, source, System.Windows.Input.Key.Enter).Should().BeTrue();
            vm.SyncOffsetMs.Should().Be(250);
            slider.Value.Should().Be(250);
            vm.SyncOffsetText.Should().Be("+250 ms");
            box.Text.Should().Be("+250");

            // 範囲の外は丸めて、欄にも丸めた値。
            box.Text = "5000";
            Key(window, box, source, System.Windows.Input.Key.Enter);
            vm.SyncOffsetMs.Should().Be(1000);
            box.Text.Should().Be("+1000");

            // 数字以外・小数は反映せず、前の値に戻す。
            box.Text = "abc";
            Key(window, box, source, System.Windows.Input.Key.Enter);
            vm.SyncOffsetMs.Should().Be(1000);
            box.Text.Should().Be("+1000");
            box.Text = "12.5";
            Key(window, box, source, System.Windows.Input.Key.Enter);
            vm.SyncOffsetMs.Should().Be(1000);
            box.Text.Should().Be("+1000");

            // Esc は反映せず元の値に戻す。
            box.Text = "-300";
            Key(window, box, source, System.Windows.Input.Key.Escape).Should().BeTrue();
            vm.SyncOffsetMs.Should().Be(1000);
            box.Text.Should().Be("+1000");

            // 欄を離れたら反映。
            box.Text = "-300";
            Invoke(window, "SyncOffsetInputBox_LostKeyboardFocus", box,
                new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, null));
            vm.SyncOffsetMs.Should().Be(-300);
            slider.Value.Should().Be(-300);

            // つまみで変えたら欄もそろう。
            slider.Value = 42;
            vm.SyncOffsetMs.Should().Be(42);
            box.Text.Should().Be("+42");
            vm.SyncOffsetText.Should().Be("+42 ms");

            // 文字のキー（スペース）は欄が受け取り、何も反映しない。
            Key(window, box, source, System.Windows.Input.Key.Space).Should().BeFalse();
            vm.SyncOffsetMs.Should().Be(42);
        }
        finally
        {
            source.Dispose();
            window.Dispose();
            window.Close();
            provider.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    });

    private static bool Key(MainWindow window, TextBox box, HwndSource source, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
        Invoke(window, "SyncOffsetInputBox_KeyDown", box, args);
        return args.Handled;
    }

    private static void Invoke(MainWindow window, string method, object sender, EventArgs args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [sender, args]);

    private static Task OnUi(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
