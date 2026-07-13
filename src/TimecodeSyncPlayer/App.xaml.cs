using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private IServiceProvider? _services;

    public IServiceProvider? Services => _services;

    internal static void ConfigureServices(IServiceCollection services)
    {
        // Native wrappers
        services.AddSingleton<IMpvApi, MpvApi>();
        services.AddSingleton<IMpvRenderApi, MpvRenderApi>();

        // Core services
        services.AddSingleton<IMediaDurationReader, MediaDurationReader>();
        services.AddSingleton<ILtcMonitor, LtcAudioMonitor>();
        services.AddSingleton<PlaylistState>();
        services.AddSingleton<ISyncDecisionEngine, SyncDecisionEngine>();
        services.AddSingleton<ITimecodeSyncSeekState, TimecodeSyncSeekState>();
        services.AddSingleton<TimecodeSyncService>();
        services.AddSingleton<PlaylistDurationBackfillService>();
        services.AddSingleton<PlaylistLoadCoordinator>();
        services.AddSingleton<GapPlaybackCommandExecutor>();
        services.AddSingleton<MpvStartupPropertyApplier>();
        services.AddSingleton<ITimecodeFpsSelector, TimecodeFpsSelector>();
        services.AddSingleton<TimecodeFrameDiagnostics>();
        services.AddSingleton<LtcFrameProcessor>();
        services.AddSingleton<PixelBufferManager>();
        services.AddSingleton<GapFreezeHandler>();
        services.AddSingleton<SpoutFramePublisher>();
        services.AddSingleton<MpvSessionInitializer>();
        services.AddSingleton<ProjectLoadApplicator>();
        services.AddSingleton<StartupBufferInitializer>();
        services.AddSingleton<RenderedFrameFreezeBufferCopier>();
        services.AddSingleton<RenderFramePerformanceRecorder>();

        // State & utilities
        services.AddSingleton(_ => AppSettingsManager.Instance);
        services.AddSingleton<IRenderUpdateScheduler, RenderUpdateScheduler>();
        services.AddSingleton(_ => new PlaybackPerformanceStats(TimeSpan.FromSeconds(2)));
        services.AddSingleton(_ => new OsdUpdateState(TimeSpan.FromMilliseconds(250)));
        services.AddSingleton<ISeekBarUpdateState, SeekBarUpdateState>();
        services.AddSingleton<ISpoutOutput, SpoutOutput>();

        // MainWindow (resolved via DI)
        services.AddSingleton<MainWindow>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = false
        });

        // DLL Hijacking 防止: DLL検索パスをアプリケーションディレクトリに制限
        SetDllDirectory(AppContext.BaseDirectory);

        string logDir  = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, "timecodesyncplayer-.log");

#if DEBUG
        var minLevel = Serilog.Events.LogEventLevel.Debug;
#else
        var minLevel = Serilog.Events.LogEventLevel.Information;
#endif

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "UnhandledException");
            Log.CloseAndFlush();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "DispatcherUnhandledException");
            MessageBox.Show(
                "予期しないエラーが発生しました。アプリケーションを終了します。",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Log.CloseAndFlush();
            Shutdown();
        };

        Log.Information("=== TimecodeSyncPlayer 起動 === ログ: {Path}", logPath);

        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== TimecodeSyncPlayer 終了 ===");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
