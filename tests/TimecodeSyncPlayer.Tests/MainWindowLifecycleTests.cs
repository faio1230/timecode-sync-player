using System.Reflection;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace TimecodeSyncPlayer.Tests;

[Collection("WpfWindow")]
public sealed class MainWindowLifecycleTests
{
    [Fact]
    public Task PausedFullscreenInitialImage_AndReopenedWindowUseExternalBitmapInsteadOfPreview() => OnUi(async () =>
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            var session = (RenderSession)typeof(MainWindow).GetField("_renderSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            session.InitializeFrameRenderer(); // No Loaded/native/audio startup and no shown window.
            var renderer = (FrameRenderer)typeof(RenderSession).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var previewImage = (System.Windows.Controls.Image)window.FindName("VideoImage");
            var source = new byte[1920 * 1080 * 4];
            Array.Fill(source, (byte)31);
            renderer.UpdateFromPixels(source, 1920, 1080);
            Assert.Null(previewImage.Source); // Full-resolution event must not update the main preview.
            renderer.QueuePreviewFromCurrentBitmap("frozen");
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (previewImage.Source == null && timeout.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            var reduced = Assert.IsType<WriteableBitmap>(previewImage.Source);
            Assert.Equal((960, 540), (reduced.PixelWidth, reduced.PixelHeight));
            var external = session.CurrentExternalBitmap!;
            Assert.NotSame(reduced, external);
            var create = typeof(MainWindow).GetMethod("CreateFullscreenOutputWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var target = new DisplayTarget("test-only-monitor", new DisplayBounds(0, 0, 1920, 1080), true);
            for (int i = 0; i < 2; i++)
            {
                var fullscreen = (FullscreenOutputWindow)create.Invoke(window, [target])!;
                try
                {
                    var image = (System.Windows.Controls.Image)fullscreen.FindName("FullscreenImage");
                    Assert.Same(external, image.Source);
                    Assert.Equal((1920, 1080), (external.PixelWidth, external.PixelHeight));
                }
                finally { fullscreen.Close(); } // Reopening starts from the retained full-resolution image without another frame.
            }
        }
        finally { window.Dispose(); window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LtcCallbacks_QueuedBeforeOrReceivedAfterDisposeDoNotChangeUi(bool receivedAfterDispose) => OnUi(async () =>
    {
        // Construct the real Window without showing it, so no native/audio startup occurs.
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            window.ViewModel.Sync.IsLtcRunning = true;
            window.ViewModel.Sync.LtcTimecodeText = "before shutdown";
            string format = window.ViewModel.Sync.LtcFormatText;
            if (receivedAfterDispose) window.Dispose();
            // Invoke event boundaries without starting a hardware monitor or the Loaded pipeline.
            typeof(MainWindow).GetMethod("LtcMonitor_FrameReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, new LtcFrameReceivedEventArgs(new LtcTimecode(0, 0, 1, 0, false), 25, 1)]);
            typeof(MainWindow).GetMethod("LtcMonitor_Stopped", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, new InvalidOperationException("late stop")]);
            window.Dispose();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            window.ViewModel.Sync.IsLtcRunning.Should().BeTrue();
            window.ViewModel.Sync.LtcTimecodeText.Should().Be("before shutdown");
            window.ViewModel.Sync.LtcFormatText.Should().Be(format);
        }
        finally { window.Close(); }
    });

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
