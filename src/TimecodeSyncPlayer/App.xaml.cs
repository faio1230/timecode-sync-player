using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

public partial class App : Application
{
    static App() => NativeLibraryResolver.Register();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private IServiceProvider? _services;

    public IServiceProvider? Services => _services;

    internal static void ConfigureServices(IServiceCollection services)
    {
        // Native wrappers. 出荷構成（GStreamer + Gpu）の実装を直接解決する。
        services.AddSingleton<GstNativeApi>();
        services.AddSingleton<IGstNativeApi>(sp => sp.GetRequiredService<GstNativeApi>());
        services.AddSingleton<GstBackendState>();
        services.AddSingleton<GstPlaybackApi>();
        services.AddSingleton<GstRenderUpdateSource>();
        services.AddSingleton<GstSpoutOutput>();
        services.AddSingleton<IPlaybackApi>(sp => sp.GetRequiredService<GstPlaybackApi>());
        services.AddSingleton<IRenderUpdateSource>(sp => sp.GetRequiredService<GstRenderUpdateSource>());

        // Core services
        services.AddSingleton<IMediaDurationReader, MediaDurationReader>();
        services.AddSingleton<ILtcMonitor, LtcAudioMonitor>();
        services.AddSingleton<PlaylistState>();
        services.AddSingleton<SeekLatencyCompensator>();
        services.AddSingleton<ISyncDecisionEngine>(sp => new SyncDecisionEngine(
            new SyncDecisionOptions(),
            sp.GetRequiredService<SeekLatencyCompensator>()));
        services.AddSingleton<ITimecodeSyncSeekState, TimecodeSyncSeekState>();
        services.AddSingleton(sp => new TimecodeSyncService(
            sp.GetRequiredService<ISyncDecisionEngine>(),
            sp.GetRequiredService<ITimecodeSyncSeekState>(),
            null,
            sp.GetRequiredService<SeekLatencyCompensator>()));
        services.AddSingleton<PlaylistDurationBackfillService>();
        services.AddSingleton<PlaylistLoadCoordinator>();
        services.AddSingleton<GapPlaybackCommandExecutor>();
        services.AddSingleton<ITimecodeFpsSelector, TimecodeFpsSelector>();
        services.AddSingleton<TimecodeFrameDiagnostics>();
        services.AddSingleton<LtcFrameProcessor>();
        services.AddSingleton<GapFreezeHandler>();
        services.AddSingleton<ProjectLoadApplicator>();

        // State & utilities
        services.AddSingleton(_ => AppSettingsManager.Instance);
        services.AddSingleton(_ => new PlaybackPerformanceStats(TimeSpan.FromSeconds(2)));
        services.AddSingleton<ISeekBarUpdateState, SeekBarUpdateState>();
        services.AddSingleton<ISpoutOutput>(sp => sp.GetRequiredService<GstSpoutOutput>());
        services.AddSingleton<OutputBackendState>();

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
        // D15/O1: shim の LOG 出力先（TCS_LOG_FILE）を同じ logs ディレクトリへ固定する（GStreamer 初期化前）。
        GstNativeLibraryResolver.ConfigureLogFile(logDir);
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

        SyncAccuracyTrace.Current = SyncAccuracyTrace.Create(
            Environment.GetEnvironmentVariable(SyncAccuracyTrace.EnvironmentVariable));

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

        Log.Information(
            "=== TimecodeSyncPlayer v{Version} 起動 === ログ: {Path}",
            ApplicationVersion.Current,
            logPath);

        var settingsManager = _services.GetRequiredService<AppSettingsManager>();
        settingsManager.LoadAsync().GetAwaiter().GetResult();

        var outputBackendState = _services.GetRequiredService<OutputBackendState>();
        outputBackendState.Initialize(settingsManager.Current.OutputBackend);

        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SyncAccuracyTrace.Current.Dispose();
        SyncAccuracyTrace.Current = SyncAccuracyTrace.Disabled;
        // MainWindow owns ordered shutdown of its RenderSession and the shared LTC/Spout services.
        // Keep the provider rooted until process exit; disposing it here would bypass that order
        // and retry native resources whose release may have failed in Window_Closing.
        Log.Information("=== TimecodeSyncPlayer 終了 ===");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
