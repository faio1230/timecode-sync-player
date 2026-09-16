using System.Reflection;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Tests.Gst;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// Gap フリーズの「キャプチャ完了」は GPU 合成層が進入時にソース画像を保存する（SaveFreeze）。
/// ここでは、シーク完了後にコールバックが来なくてもタイマー経由で FreezeComplete へ遷移すること、
/// 再生が動いている間は確定しないことを固定する。
/// </summary>
[Collection("WpfWindow")]
public sealed class MainWindowGapFreezeRetryTests
{
    [Fact]
    public Task FinalCallbackWhileSeeking_TimerCompletesFreezeWithoutAnotherCallback() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.PlaybackApi.Seeking = true;
        await fixture.ProcessFinalCallback();
        fixture.Handler.CurrentState.Should().Be(GapState.EnteringFreeze);

        fixture.PlaybackApi.Seeking = false;
        fixture.Tick(); // ネイティブシーク完了後にレンダーコールバックは届かない。
        await WaitUntil(() => fixture.Handler.CurrentState == GapState.FreezeComplete);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        fixture.Handler.CurrentState.Should().Be(GapState.FreezeComplete);
    });

    [Fact]
    public Task TimerRetry_WhileNativePlaybackUnpaused_DoesNotConfirmMovingFrame() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.PlaybackApi.Paused = false;
        fixture.Tick();
        await fixture.Session.ProcessUpdateAsync((_, _) => Task.CompletedTask);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        fixture.Handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        fixture.Handler.CachedTrackId.Should().BeNull();
    });

    [Theory]
    [InlineData("resume")]
    [InlineData("seek")]
    [InlineData("completed-seek")]
    public Task TimerRetry_NativePlaybackChanges_DoesNotConfirmObsoleteFrame(string change) => OnUi(async () =>
    {
        using var fixture = new Fixture();
        if (change == "resume") fixture.PlaybackApi.Paused = false;
        else if (change == "seek") fixture.PlaybackApi.Seeking = true;
        else fixture.PlaybackApi.TimePos = 9;
        fixture.Tick();
        await fixture.Session.ProcessUpdateAsync((_, _) => Task.CompletedTask);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        fixture.Handler.CurrentState.Should().NotBe(GapState.FreezeComplete);
        fixture.Handler.CachedTrackId.Should().BeNull();
    });

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public readonly RenderApi RenderUpdateSource = new();
        public readonly SpoutOutput Spout = new();
        public readonly FakePlaybackApi PlaybackApi = new() { Path = "C:/clip.mp4", Paused = true, TimePos = 9.9 };
        public MainWindow Window { get; }
        public GapFreezeHandler Handler { get; }
        public RenderSession Session { get; }

        public Fixture()
        {
            var services = new ServiceCollection();
            App.ConfigureServices(services);
            var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
            services.AddSingleton<IGstNativeApi>(native);
            services.AddSingleton<IRenderUpdateSource>(RenderUpdateSource);
            services.AddSingleton<ISpoutOutput>(Spout);
            services.AddSingleton<IPlaybackApi>(PlaybackApi);
            _provider = services.BuildServiceProvider();
            Window = _provider.GetRequiredService<MainWindow>();
            Handler = _provider.GetRequiredService<GapFreezeHandler>();
            Session = (RenderSession)typeof(MainWindow)
                .GetField("_renderSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
            GstBackendState backend = _provider.GetRequiredService<GstBackendState>();
            backend.EnsurePlayer().Should().BeTrue();
            typeof(MainWindow).GetField("_fps", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Window, 30d);
            Session.Create(new IntPtr(1)).Should().BeTrue();
            Handler.EnterFreezeCapture(Guid.NewGuid(), 9.9, "C:/clip.mp4");
        }

        public Task ProcessFinalCallback() => (Task)Method("ProcessRenderFrameUpdateAsync")
            .Invoke(Window, [Session.CaptureGeneration(), true])!;
        public void Tick() => Method("OnTick").Invoke(Window, [null, EventArgs.Empty]);
        private static MethodInfo Method(string name) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose()
        {
            Window.Dispose();
            Window.Close();
            _provider.Dispose();
        }
    }

    private sealed class RenderApi : IRenderUpdateSource
    {
        public ulong FrameUpdateFlag => 1;
        public bool TryCreateContext(IntPtr player, out IntPtr context)
        { context = new IntPtr(2); return true; }
        public ulong ConsumeUpdate(IntPtr ctx) => 0; // 一時停止中: 後続の FRAME 仕事はない。
        public void SetUpdateCallback(IntPtr ctx, RenderUpdateFn? callback) { }
        public void FreeContext(IntPtr ctx) { }
    }

    private sealed class SpoutOutput : ISpoutOutput
    {
        public bool IsEnabled { get; set; } = true;
        public bool IsAvailable => true;
        public bool TryInitialize() => true;
        public void Dispose() { }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }

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
