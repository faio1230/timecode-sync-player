using System.Reflection;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Tests.Gst;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.3 段 3a/3b: §6 の 2（<c>BeginFileLoad</c> を通らない読み込み）の赤いテスト。
/// 段 0 の表 <c>LatchLifetimeTable</c> の <c>FileLoadWithoutBegin</c> で「意図 = 消える」の
/// 9 行のうち、この組み立てで前提を作れるサービス側の 3 キー
/// （<c>pendingSeek</c>・<c>positionUntrusted</c>・<c>fileLoadReleasePending</c>）を確かめる。
/// 段 3b で #1（GPU 復旧）と #2（自動送り）は直したので緑。ギャップの #3・#4 は
/// 利用者の判断待ちのため <see cref="FactAttribute.Skip"/> を付けたままにする。
/// </summary>
[Collection("WpfWindow")]
public sealed class V053LoadPathTests
{
    [Fact]
    public Task GpuRecoveryPositionLoad_ClearsLoadLatches() => OnUi(() =>
    {
        using var f = new Fixture();
        f.ArrangeServiceLatches();

        bool ok = f.LoadLikeGpuRecovery("C:/clip.mp4", 14.58);

        ok.Should().BeTrue();
        f.PendingSeek.Should().BeFalse("§6 の 2: 読み込みでシークの保留を捨てる（意図 = 消える）");
        f.PositionUntrusted.Should().BeFalse("§6 の 2: 読み込みで位置の信頼を初期化する（意図 = 消える）");
        f.FileLoadReleasePending.Should().BeFalse("§6 の 2: 読み込みで解除の回収待ちを下ろす（意図 = 消える）");
        return Task.CompletedTask;
    });

    [Fact]
    public Task AutoAdvanceLocatedLoad_ClearsLoadLatches() => OnUi(() =>
    {
        using var f = new Fixture();
        f.SeedContinueAutoAdvance();
        f.ArrangeServiceLatches();

        f.AdvancePlaylistAtEnd(5.0);

        f.PlaybackApi.Loads.Should().ContainSingle(load =>
            load.Path == "C:/b.mp4" && load.StartSeconds == 10.0,
            "MediaIn > 0 の自動送りは位置つきで読み込む");
        f.PendingSeek.Should().BeFalse("§6 の 2: 読み込みでシークの保留を捨てる（意図 = 消える）");
        f.PositionUntrusted.Should().BeFalse("§6 の 2: 読み込みで位置の信頼を初期化する（意図 = 消える）");
        f.FileLoadReleasePending.Should().BeFalse("§6 の 2: 読み込みで解除の回収待ちを下ろす（意図 = 消える）");
        return Task.CompletedTask;
    });

    [Fact(Skip = "v0.5.3（利用者の判断待ち: ギャップの読み込みは別の口が要る）")]
    public Task GapFreezePreviousTrackLoad_ClearsLoadLatches() => OnUi(() =>
    {
        using var f = new Fixture();
        f.ArrangeServiceLatches();

        f.LoadPreviousTrackFinalFrameForGapFreeze();

        f.PlaybackApi.Loads.Should().Contain(load =>
            load.Path == "C:/prev.mp4" && load.StartSeconds == 4.96 && load.Paused,
            "ギャップの直前トラック読み込みは一時停止の位置つきロード");
        f.PendingSeek.Should().BeFalse("§6 の 2: 読み込みでシークの保留を捨てる（意図 = 消える）");
        f.PositionUntrusted.Should().BeFalse("§6 の 2: 読み込みで位置の信頼を初期化する（意図 = 消える）");
        f.FileLoadReleasePending.Should().BeFalse("§6 の 2: 読み込みで解除の回収待ちを下ろす（意図 = 消える）");
        return Task.CompletedTask;
    });

    [Fact(Skip = "v0.5.3（利用者の判断待ち: ギャップの読み込みは別の口が要る）")]
    public Task GapFreezePathGuardReload_ClearsLoadLatches() => OnUi(() =>
    {
        using var f = new Fixture();
        f.ArrangeServiceLatches();
        f.PlaybackApi.Path = "C:/other.mp4";

        bool expected = f.IsCurrentPathExpectedForGapFreeze();

        expected.Should().BeFalse("現在パスが期待と違うので読み直しを出す");
        f.PlaybackApi.Loads.Should().Contain(load =>
            load.Path == "C:/clip.mp4" && load.StartSeconds == 9.9 && load.Paused,
            "Freeze の取り込みの読み直しは一時停止の位置つきロード");
        f.PendingSeek.Should().BeFalse("§6 の 2: 読み込みでシークの保留を捨てる（意図 = 消える）");
        f.PositionUntrusted.Should().BeFalse("§6 の 2: 読み込みで位置の信頼を初期化する（意図 = 消える）");
        f.FileLoadReleasePending.Should().BeFalse("§6 の 2: 読み込みで解除の回収待ちを下ろす（意図 = 消える）");
        return Task.CompletedTask;
    });

    [Fact]
    public Task GpuRecoveryPositionLoad_RecordsFileLoadFromGpuRecovery() => OnUi(() =>
    {
        using var f = new Fixture();
        using var capture = new LoggerCapture();

        bool ok = f.LoadLikeGpuRecovery("C:/clip.mp4", 14.58);

        ok.Should().BeTrue();
        capture.FileLoads().Should().Equal("FileLoad/gpu-recovery");
        return Task.CompletedTask;
    });

    [Fact]
    public Task AutoAdvanceLocatedLoad_RecordsFileLoadFromAutoAdvance() => OnUi(() =>
    {
        using var f = new Fixture();
        f.SeedContinueAutoAdvance();
        using var capture = new LoggerCapture();

        f.AdvancePlaylistAtEnd(5.0);

        capture.FileLoads().Should().Equal("FileLoad/auto-advance");
        return Task.CompletedTask;
    });

    [Fact]
    public Task AutoAdvanceMediaInZero_DoesNotRecordFileLoadTwice() => OnUi(() =>
    {
        using var f = new Fixture();
        f.SeedContinueAutoAdvance(nextMediaInSeconds: 0);
        using var capture = new LoggerCapture();

        f.AdvancePlaylistAtEnd(5.0);

        f.PlaybackApi.Loads.Should().ContainSingle(load =>
            load.Path == "C:/b.mp4" && load.StartSeconds == null,
            "MediaIn = 0 の自動送りは位置なしロード");
        capture.FileLoads().Should().Equal(new[] { "FileLoad/load" },
            "位置なしは PlaybackOperationsCoordinator の入口だけを通る（二重にしない）");
        return Task.CompletedTask;
    });

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly ServiceProvider _provider;

        public readonly RenderApi RenderUpdateSource = new();
        public readonly SpoutOutput Spout = new();
        public readonly FakePlaybackApi PlaybackApi = new() { Path = "C:/clip.mp4", Paused = true, TimePos = 9.9 };
        public MainWindow Window { get; }
        public GapFreezeHandler Handler { get; }
        public RenderSession Session { get; }
        public TimecodeSyncService Sync { get; }
        public PlaylistState Playlist { get; }

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
            Sync = _provider.GetRequiredService<TimecodeSyncService>();
            Playlist = _provider.GetRequiredService<PlaylistState>();
            Session = (RenderSession)typeof(MainWindow).GetField("_renderSession", Private)!.GetValue(Window)!;
            GstBackendState backend = _provider.GetRequiredService<GstBackendState>();
            backend.EnsurePlayer().Should().BeTrue();
            typeof(MainWindow).GetField("_fps", Private)!.SetValue(Window, 25d);
            Session.Create(new IntPtr(1)).Should().BeTrue();
            Handler.EnterFreezeCapture(Guid.NewGuid(), 9.9, "C:/clip.mp4");
        }

        public bool PendingSeek => Sync.SeekState.HasPendingSeek;

        public bool PositionUntrusted => Sync.LatchSnapshot()["positionUntrusted"];

        public bool FileLoadReleasePending => Sync.LatchSnapshot()["fileLoadReleasePending"];

        /// <summary>§6 の 2 の 9 行のうち、サービス側の 3 キーを立てる。</summary>
        public void ArrangeServiceLatches()
        {
            Sync.BeginFileLoad(0, 0);
            Sync.TryMarkFileLoaded(1.0, 2).Should().BeTrue("前提: 読み込みの解除（回収待ちを作る）");
            Sync.ReportSeekSent(5.0);

            FileLoadReleasePending.Should().BeTrue("前提: 解除の回収待ち");
            PendingSeek.Should().BeTrue("前提: 保留シーク");
            PositionUntrusted.Should().BeTrue("前提: 位置が未信頼");
        }

        /// <summary>Continue + 同期 OFF で、次トラックへ自動送りできる配置にする（MediaIn は指定可）。</summary>
        public void SeedContinueAutoAdvance(double nextMediaInSeconds = 10)
        {
            Playlist.Tracks.Add(new PlaylistTrack(
                Guid.NewGuid(), "C:/a.mp4", "a", TimeSpan.Zero, null, TimeSpan.Zero,
                TimeSpan.FromSeconds(5), TimeSpan.Zero, 25, true));
            Playlist.Tracks.Add(new PlaylistTrack(
                Guid.NewGuid(), "C:/b.mp4", "b", TimeSpan.FromSeconds(nextMediaInSeconds), null, TimeSpan.Zero,
                TimeSpan.FromSeconds(nextMediaInSeconds + 5), TimeSpan.Zero, 25, true));
            Playlist.Select(0).Should().BeTrue();
            Window.ViewModel.Sync.SyncModeIndex = 1;
            Window.ViewModel.Sync.SyncEnabled = false;
            // 終端の自動送りは再生中にだけ起きる（_playbackControl.IsPaused が false）。
            ((PlaybackControlState)typeof(MainWindow).GetField("_playbackControl", Private)!
                .GetValue(Window)!).SetPaused(false);
            typeof(MainWindow).GetField("_loadedTrackId", Private)!.SetValue(Window, Playlist.Tracks[0].Id);
            typeof(MainWindow).GetField("_duration", Private)!.SetValue(Window, 5.0);
            typeof(MainWindow).GetField("_endAdvanceTriggered", Private)!.SetValue(Window, false);
        }

        /// <summary>経路 #1 と同じ入口: 現在トラックを position で読み直す（GPU 復旧の再ロード）。</summary>
        public bool LoadLikeGpuRecovery(string path, double position)
        {
            Playlist.Tracks.Add(new PlaylistTrack(
                Guid.NewGuid(), path, "clip", TimeSpan.Zero, null, TimeSpan.Zero,
                TimeSpan.FromSeconds(30), TimeSpan.Zero, 25, true));
            Playlist.Select(0).Should().BeTrue();
            return (bool)Method("ReloadCurrentTrackAfterGpuRecovery").Invoke(Window, [position])!;
        }

        public void AdvancePlaylistAtEnd(double position) =>
            Method("TryAdvancePlaylistAtEnd").Invoke(Window, [position]);

        public void LoadPreviousTrackFinalFrameForGapFreeze()
        {
            var coordinator = (GapEnterCoordinator)Method("CreateGapEnterCoordinator").Invoke(Window, null)!;
            var track = new PlaylistTrack(
                Guid.NewGuid(), "C:/prev.mp4", "prev", TimeSpan.Zero, null, TimeSpan.Zero,
                TimeSpan.FromSeconds(5), TimeSpan.Zero, 25, true);
            coordinator.LoadPreviousTrackFinalFrameForGapFreeze(track, 4.96, 5.0, 25.0);
        }

        public bool IsCurrentPathExpectedForGapFreeze() =>
            (bool)Method("IsCurrentPathExpectedForGapFreeze").Invoke(Window, null)!;

        private static MethodInfo Method(string name) =>
            typeof(MainWindow).GetMethod(name, Private)!;

        public void Dispose()
        {
            Window.Dispose();
            Window.Close();
            _provider.Dispose();
        }
    }

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private sealed class LoggerCapture : IDisposable
    {
        private readonly ILogger _previous;
        private readonly ListSink _sink = new();

        public LoggerCapture()
        {
            _previous = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(_sink).CreateLogger();
        }

        /// <summary>"Sync lifecycle: FileLoad" の行だけを "できごと/source" の列にする。</summary>
        public List<string> FileLoads()
        {
            lock (_sink.Events)
                return _sink.Events
                    .Where(e => e.MessageTemplate.Text.StartsWith("Sync lifecycle:", StringComparison.Ordinal))
                    .Where(e => Scalar(e, "Event") == "FileLoad")
                    .Select(e => $"{Scalar(e, "Event")}/{Scalar(e, "Source")}")
                    .ToList();
        }

        public void Dispose() => Log.Logger = _previous;

        private static string Scalar(LogEvent e, string name) =>
            e.Properties.TryGetValue(name, out LogEventPropertyValue? v) && v is ScalarValue s
                ? s.Value?.ToString() ?? ""
                : "";
    }

    private sealed class RenderApi : IRenderUpdateSource
    {
        public ulong FrameUpdateFlag => 1;
        public bool TryCreateContext(IntPtr player, out IntPtr context)
        { context = new IntPtr(2); return true; }
        public ulong ConsumeUpdate(IntPtr ctx) => 0;
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
