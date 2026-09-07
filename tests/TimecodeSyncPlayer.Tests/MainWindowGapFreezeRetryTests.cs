using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

[Collection("WpfWindow")]
public sealed class MainWindowGapFreezeRetryTests
{
    [Fact]
    public Task FinalCallbackWhileSeeking_TimerPublishesFinalPixelsWithoutAnotherCallback() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.Api.Seeking = true;
        await fixture.ProcessFinalCallback();
        fixture.Handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        fixture.Spout.Pixels.Clear();

        fixture.Api.Seeking = false;
        fixture.Tick(); // No render callback is delivered after the native seek completes.
        await WaitUntil(() => fixture.Handler.CurrentState == GapState.FreezeComplete);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        fixture.Spout.Pixels.Should().Contain((byte)73, "timer completion must publish the captured image to Spout");
        var bitmap = ((Image)fixture.Window.FindName("VideoImage")).Source.Should().BeOfType<WriteableBitmap>().Which;
        byte[] pixels = new byte[16];
        bitmap.CopyPixels(pixels, 8, 0);
        pixels[0].Should().Be(73, "the WPF output must show the captured image without another callback");
        fixture.RenderApi.RenderCount.Should().Be(1);
    });

    [Fact]
    public Task TimerRetry_WhileNativePlaybackUnpaused_DoesNotConfirmMovingFrame() => OnUi(async () =>
    {
        using var fixture = new Fixture();
        fixture.Api.Paused = false;
        fixture.Tick();
        // Flush the native worker and then the UI continuation before checking the result.
        await fixture.Session.ProcessUpdateAsync((_, _) => Task.CompletedTask);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        fixture.Handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        fixture.Handler.CachedTrackId.Should().BeNull();
        fixture.Spout.Pixels.Should().BeEmpty();
        fixture.RenderApi.RenderCount.Should().Be(0);
    });

    [Theory]
    [InlineData("resume")]
    [InlineData("seek")]
    [InlineData("completed-seek")]
    public Task TimerRetry_NativePlaybackChangesWhileRendering_DoesNotCacheOrPublishObsoleteFrame(string change) => OnUi(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        fixture.RenderApi.Release = release;
        fixture.Tick();
        try
        {
            await fixture.RenderApi.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "resume") fixture.Api.Paused = false;
            else if (change == "seek") fixture.Api.Seeking = true;
            else fixture.Api.Position = 9;
        }
        finally { release.Set(); }
        await fixture.Session.ProcessUpdateAsync((_, _) => Task.CompletedTask);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        fixture.Handler.CurrentState.Should().NotBe(GapState.FreezeComplete);
        fixture.Handler.CachedTrackId.Should().BeNull();
        fixture.Buffers.CachedGapFreezeFrameBuffer.Should().BeNull();
        fixture.Spout.Pixels.Should().BeEmpty();
    });

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        public readonly NativeApi Api = new();
        public readonly RenderApi RenderApi = new();
        public readonly SpoutOutput Spout = new();
        public MainWindow Window { get; }
        public GapFreezeHandler Handler { get; }
        public RenderSession Session { get; }
        public PixelBufferManager Buffers => (PixelBufferManager)typeof(RenderSession)
            .GetField("_buffers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Session)!;

        public Fixture()
        {
            var services = new ServiceCollection();
            App.ConfigureServices(services);
            services.AddSingleton<IMpvApi>(Api);
            services.AddSingleton<IMpvRenderApi>(RenderApi);
            services.AddSingleton<ISpoutOutput>(Spout);
            _provider = services.BuildServiceProvider();
            Window = _provider.GetRequiredService<MainWindow>();
            Handler = _provider.GetRequiredService<GapFreezeHandler>();
            Session = Field<RenderSession>("_renderSession");
            SetField("_mpv", new IntPtr(1));
            SetField("_metadataFetched", true);
            SetField("_fps", 30d);
            Session.Create(new IntPtr(1)).Should().BeTrue();
            Session.AllocateParameters();
            Session.InitializeFrameRenderer();
            Session.InitializeStartupBuffer();
            Session.Width = 2;
            Session.Height = 2;
            Handler.EnterFreezeCapture(Guid.NewGuid(), 9.9, "C:/clip.mp4");
        }

        public Task ProcessFinalCallback() => (Task)Method("ProcessRenderFrameUpdateAsync")
            .Invoke(Window, [Session.CaptureGeneration(), true])!;
        public void Tick() => Method("OnTick").Invoke(Window, [null, EventArgs.Empty]);
        private T Field<T>(string name) => (T)typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
        private void SetField(string name, object value) => typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, value);
        private static MethodInfo Method(string name) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose()
        {
            Window.Dispose();
            Window.Close();
            _provider.Dispose();
        }
    }

    private sealed class NativeApi : IMpvApi
    {
        public bool Seeking;
        public bool Paused = true;
        public double Position = 9.9;
        public IntPtr Create() => new(1);
        public int Initialize(IntPtr ctx) => 0;
        public void TerminateDestroy(IntPtr ctx) { }
        public int SetPropertyString(IntPtr ctx, string name, string value) => 0;
        public int GetProperty(IntPtr ctx, string name, int format, out double result)
        { result = name == "duration" ? 10 : Position; return 0; }
        public string GetPropertyString(IntPtr ctx, string name) => name switch
        {
            "seeking" => Seeking ? "yes" : "no",
            "pause" => Paused ? "yes" : "no",
            "path" => "C:/clip.mp4",
            _ => ""
        };
        public int CommandString(IntPtr ctx, string args) => 0;
        public void Free(IntPtr data) { }
        public int FormatDouble => 5;
    }

    private sealed class RenderApi : IMpvRenderApi
    {
        public int RenderCount;
        public ManualResetEventSlim? Release;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MpvRenderParamApiType => 1;
        public int MpvRenderParamSwSize => 17;
        public int MpvRenderParamSwFormat => 18;
        public int MpvRenderParamSwStride => 19;
        public int MpvRenderParamSwPointer => 20;
        public string MpvRenderApiTypeSw => "sw";
        public ulong MpvRenderUpdateFrame => 1;
        public int RenderContextCreate(out IntPtr res, IntPtr mpv, MpvRenderNative.MpvRenderParam[] parameters)
        { res = new IntPtr(2); return 0; }
        public ulong RenderContextUpdate(IntPtr ctx) => 0; // Paused: no later FRAME work.
        public int RenderContextRender(IntPtr ctx, MpvRenderNative.MpvRenderParam[] parameters)
        {
            Interlocked.Increment(ref RenderCount);
            Started.TrySetResult();
            Release?.Wait();
            Marshal.WriteByte(parameters.Single(p => p.Type == MpvRenderParamSwPointer).Data, 73);
            return 0;
        }
        public void RenderContextSetUpdateCallback(IntPtr ctx, MpvRenderNative.MpvRenderUpdateFn callback, IntPtr callbackCtx) { }
        public void RenderContextFree(IntPtr ctx) { }
    }

    private sealed class SpoutOutput : ISpoutOutput
    {
        public readonly List<byte> Pixels = [];
        public bool IsEnabled { get; set; } = true;
        public bool IsAvailable => true;
        public bool TryInitialize() => true;
        public void SendFrame(IntPtr pixels, int width, int height) => Pixels.Add(Marshal.ReadByte(pixels));
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
