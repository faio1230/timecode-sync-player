using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// tcs_gstreamer.dll の P/Invoke とロード解決。
/// DLL 自体はアプリの出力ディレクトリ、GStreamer の実行時 DLL は
/// GSTREAMER_1_0_ROOT_MSVC_X86_64（または既定のインストール先）の bin から解決する。
/// </summary>
internal static class GstNative
{
    internal const string Lib = "tcs_gstreamer.dll";
    internal const int TcsErrEnded = -6;
    /* tcs_gstreamer.h の TCS_ERR_TIMEOUT（tcs_probe_duration の待ちの上限切れ、v0.6.6）。 */
    internal const int TcsErrTimeout = -8;
    /* tcs_gstreamer.h の tcs_decode_mode と同じ値。 */
    internal const int DecodeModeHardware = 0;
    internal const int DecodeModeSoftware = 1;
    /* tcs_gstreamer.h の TCS_PRORES_GPU_* と同じ値（v0.6.0）。 */
    internal const int ProResGpuAuto = 0;
    internal const int ProResGpuOn = 1;
    internal const int ProResGpuOff = 2;

    internal static bool IsGstLibrary(string libraryName) =>
        string.Equals(libraryName, Lib, StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    internal struct TcsFrameInfo
    {
        public ulong Generation;
        public ulong Seq;
        public long PtsNs;
        public int Width;
        public int Height;
        public int IsGpu;
        /// <summary>ステージ 6b: 共有リング slot (0..2)。-1 = 旧サンプルリース経路。</summary>
        public int Slot;
        /// <summary>D8: slot が属するリング世代。0 = リング外（旧サンプルリース）。</summary>
        public uint RingEpoch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TcsDeliveryEvent
    {
        public ulong Qpc;
        public ulong Seq;
        public long PtsNs;
        public long RunningNs;
        public uint CallbackUs;
        public uint Flags;
    }

    /// <summary>0.4.5-A: tcs_player_get_time_pos_ex が返す 1 スナップショット。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TcsPositionSample
    {
        public double Seconds;
        public int Basis;
        public ulong Generation;
        public double DeliveredSeconds;
        public ulong DeliveredGeneration;
        public ulong CurrentGeneration;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TcsDeliveryStats
    {
        public ulong Arrivals;
        public ulong LatestReplaced;
        public ulong QosEvents;
        public ulong DecoderOut;
        public ulong RingDropped;
        public ulong LastQpc;
    }

    /// <summary>0.4.5-C: ロング GOP 警告（キーフレーム間隔の中央値）のポーリング用スナップショット。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TcsGopStatus
    {
        public int State;
        public int Active;
        public ulong Keyframes;
        public double MedianIntervalSec;
        public double PendingSec;
        public double ThresholdSec;
        public ulong WarningQpc;
    }

    /// <summary>0.4.5-C3: 読み込み時の静的スキャン結果（再生せずにコンテナから読む）。</summary>
    internal struct TcsGopScan
    {
        public int Keyframes;
        /// <summary>1 = 上限で打ち切った。ギャップは下限としてしか使えない。</summary>
        public int Truncated;
        public double DurationSec;
        public double HeadGapSec;
        public double TailGapSec;
        public double MedianGapSec;
        public double P95GapSec;
        public double MaxGapSec;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TcsFrameNotifyDelegate(IntPtr userData, ulong generation, ulong seq);

    internal static class Imports
    {
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr tcs_player_create(
            string senderName, IntPtr externalDevice, byte[] errbuf, UIntPtr errbufLen);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void tcs_player_destroy(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_load(
            IntPtr player, byte[] utf8Path, double startSec, int paused, byte[] errbuf, UIntPtr errbufLen);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_stop(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_paused(IntPtr player, int paused);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_paused(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong tcs_player_seek(IntPtr player, double seconds);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong tcs_player_step_frame(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong tcs_player_set_generation(IntPtr player, ulong generation);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong tcs_player_get_generation(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_speed(IntPtr player, double rate);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_rate_instant(IntPtr player, double rate);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_volume(IntPtr player, double volume0To100);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_mute(IntPtr player, int mute);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_decode_mode(IntPtr player, int mode);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_prores_gpu(IntPtr player, int mode);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_time_pos(IntPtr player, out double outSec);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_time_pos_ex(
            IntPtr player, out TcsPositionSample outSample);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_duration(IntPtr player, out double outSec);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_fps(IntPtr player, out double outFps);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_path(IntPtr player, byte[] outBuf, UIntPtr outLen);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_size(IntPtr player, out int outW, out int outH);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void tcs_player_set_frame_callback(
            IntPtr player, TcsFrameNotifyDelegate? callback, IntPtr userData);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_consume_update(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_acquire(
            IntPtr player, ulong generation, out TcsFrameInfo outInfo);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_leased_texture(
            IntPtr player, out IntPtr outTexture, out uint outSubresource, out uint outDxgiFormat);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void tcs_player_release(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_publish_spout(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_send_image(
            IntPtr player, IntPtr bgra, int width, int height, int pitch);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_decoder_name(IntPtr player, byte[] outBuf, UIntPtr outLen);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_spout_ready(IntPtr player);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_drain_delivery_events(
            IntPtr player, [Out] TcsDeliveryEvent[] outEvents, uint capacity, out uint outCount);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_delivery_stats(
            IntPtr player, out TcsDeliveryStats outStats);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_get_gop_status(
            IntPtr player, out TcsGopStatus outStatus);

        // 0.4.5-C3: プレイヤー不要。コンテナを読むだけで、再生経路には触れない。
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int tcs_scan_gop(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Path, int budgetMs, out TcsGopScan outScan);

        // v0.6.6 F-7: 容器の長さだけを読む（デコーダを作らない）。プレイヤー不要で、再生経路には触れない。
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int tcs_probe_duration(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Path, int timeoutMs, out double outSeconds);

        // ステージ 6b: 共有テクスチャリングの NT ハンドル + 共有フェンス。
        // ハンドルは shim 所有（CloseHandle 禁止）。
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_ring_info(
            IntPtr player, [Out] IntPtr[] outHandles, uint capacity, out uint outCount,
            out IntPtr outFence, out int outWidth, out int outHeight);

        // D8: リングの世代。リングは解像度が変わると作り直され、epoch が +1 される。
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_ring_epoch(IntPtr player, out uint outEpoch);
    }
}

/// <summary>

/// <summary>
/// tcs_gstreamer.dll と GStreamer ランタイム DLL の検索経路。
/// アセンブリ共通の DllImportResolver は NativeLibraryResolver が所有するため、
/// ここでは判定と個別ロードだけを提供する。
/// </summary>
internal enum GstRootSource
{
    EnvironmentVariable,
    Bundled,
    ProgramFiles
}

internal readonly record struct GstRoot(string Path, GstRootSource Source);

internal static class GstNativeLibraryResolver
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    internal const string BundledDirectoryName = "gstreamer";

    /// <summary>
    /// 環境変数（明示指定）→ 配布物に同梱した gstreamer ディレクトリ → システム導入先、の順で探す。
    /// 配布物では同梱ランタイムを使い、版を固定する。
    /// </summary>
    public static GstRoot? FindGstRoot() => ResolveRoot(
        Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MSVC_X86_64"),
        System.IO.Path.Combine(AppContext.BaseDirectory, BundledDirectoryName),
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "gstreamer", "1.0", "msvc_x86_64"),
        IsGstRoot);

    internal static GstRoot? ResolveRoot(
        string? environmentRoot, string bundledRoot, string programFilesRoot, Func<string, bool> isGstRoot)
    {
        if (!string.IsNullOrEmpty(environmentRoot) && isGstRoot(environmentRoot!))
            return new GstRoot(environmentRoot!, GstRootSource.EnvironmentVariable);
        if (isGstRoot(bundledRoot))
            return new GstRoot(bundledRoot, GstRootSource.Bundled);
        return isGstRoot(programFilesRoot) ? new GstRoot(programFilesRoot, GstRootSource.ProgramFiles) : null;
    }

    internal static bool IsGstRoot(string dir) =>
        !string.IsNullOrEmpty(dir) &&
        (System.IO.File.Exists(System.IO.Path.Combine(dir, "bin", "gstreamer-1.0-0.dll"))
         || System.IO.File.Exists(System.IO.Path.Combine(dir, "bin", "gstreamer-1.0.dll")));

    public static string? FindGstBinDirectory()
    {
        GstRoot? root = FindGstRoot();
        return root is null ? null : System.IO.Path.Combine(root.Value.Path, "bin");
    }

    /// <summary>
    /// D15/O1: shim（tcs_gstreamer.dll）の LOG 出力先（TCS_LOG_FILE）を logs ディレクトリへ設定する。
    /// GStreamer を初期化する前に呼ぶ。利用者が既に設定していれば上書きしない。
    /// ログの肥大を避けるため、7 日より古い tcs-gst-*.log を消す。
    /// </summary>
    public static void ConfigureLogFile(string logsDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TCS_LOG_FILE")))
            {
                System.IO.Directory.CreateDirectory(logsDirectory);
                Environment.SetEnvironmentVariable("TCS_LOG_FILE",
                    System.IO.Path.Combine(logsDirectory, $"tcs-gst-{DateTime.Now:yyyyMMdd}.log"));
            }

            PruneOldLogFiles(logsDirectory, TimeSpan.FromDays(7));
        }
        catch (Exception)
        {
            // ログ出力先の設定に失敗しても起動は止めない。
        }
    }

    /// <summary>
    /// v0.5.4 K3 (3): shim は DLL ロード時に TCS_PUMP_BUDGET_MS を 1 回だけ読む
    /// （native/gst-shim/src/tcs_gstreamer.cpp:253-274）。明示が無ければ「着地の時間切れ − 0.5 秒」を
    /// 渡し、ポンプがアプリの着地より先に切れるようにする（時間切れの逆転を戻さない）。利用者が
    /// 既に設定していれば上書きしない。GStreamer（shim）の初期化前に呼ぶ。
    /// </summary>
    public static void ConfigurePumpBudget()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable)))
            {
                Environment.SetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable,
                    SeekTimeBudget.PumpBudgetMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception)
        {
            // 予算を渡せなくても起動は止めない（shim の既定で動く）。
        }
    }

    private static void PruneOldLogFiles(string logsDirectory, TimeSpan retention)
    {
        if (!System.IO.Directory.Exists(logsDirectory))
            return;

        DateTime cutoffUtc = DateTime.UtcNow - retention;
        foreach (string path in System.IO.Directory.EnumerateFiles(logsDirectory, "tcs-gst-*.log"))
        {
            try
            {
                if (System.IO.File.GetLastWriteTimeUtc(path) < cutoffUtc)
                    System.IO.File.Delete(path);
            }
            catch (Exception)
            {
                // 使用中などで消せないファイルは残す。
            }
        }
    }

    /// <summary>
    /// 同梱ランタイムを使うとき、プラグイン探索とレジストリキャッシュを同梱ディレクトリへ固定する。
    /// システムに別の GStreamer があっても混ざらない（版の固定が配布物の正しさの要件のため）。
    /// 失敗しても再生経路は止めない。
    /// </summary>
    public static void ApplyBundledPluginEnvironment(string bundledRoot)
    {
        try
        {
            string plugins = System.IO.Path.Combine(bundledRoot, "lib", "gstreamer-1.0");
            if (!System.IO.Directory.Exists(plugins))
                return;

            Environment.SetEnvironmentVariable("GST_PLUGIN_PATH", plugins);
            Environment.SetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH", plugins);

            string cacheDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TimecodeSyncPlayer");
            System.IO.Directory.CreateDirectory(cacheDirectory);
            Environment.SetEnvironmentVariable("GST_REGISTRY",
                System.IO.Path.Combine(cacheDirectory, "gstreamer-registry-x86_64.bin"));
        }
        catch (Exception)
        {
            // 環境変数を固定できなくても、システム側の GStreamer で動き続けられるようにする。
        }
    }

    /// <summary>開発機・試験で追加のプラグイン（ProRes の GPU 復号など）を置く、exe の隣のフォルダ名。</summary>
    internal const string ExtraPluginsDirectoryName = "gst-extra-plugins";

    /// <summary>
    /// v0.6.0: 同梱でないとき（環境変数・Program Files の GStreamer）だけ、追加のプラグインのフォルダを
    /// GST_PLUGIN_PATH に足した値を返す（既存の値があれば ';' で連結）。足さないときは null。
    /// 同梱のときはプラグインが lib\gstreamer-1.0 に入るので足さない。
    /// GST_PLUGIN_SYSTEM_PATH と GST_REGISTRY は触らない（システムのプラグインは今までどおり）。
    /// </summary>
    internal static string? ComposeExtraPluginPath(
        GstRootSource source, string? existingPluginPath, string extraPluginsDirectory,
        Func<string, bool> directoryExists)
    {
        if (source == GstRootSource.Bundled || !directoryExists(extraPluginsDirectory))
            return null;
        if (string.IsNullOrEmpty(existingPluginPath))
            return extraPluginsDirectory;

        foreach (string entry in existingPluginPath!.Split(';'))
        {
            if (string.Equals(entry.Trim(), extraPluginsDirectory, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return existingPluginPath + ";" + extraPluginsDirectory;
    }

    private static void ApplyExtraPluginEnvironment(GstRootSource source)
    {
        try
        {
            string? pluginPath = ComposeExtraPluginPath(
                source,
                Environment.GetEnvironmentVariable("GST_PLUGIN_PATH"),
                System.IO.Path.Combine(AppContext.BaseDirectory, ExtraPluginsDirectoryName),
                System.IO.Directory.Exists);
            if (pluginPath is not null)
                Environment.SetEnvironmentVariable("GST_PLUGIN_PATH", pluginPath);
        }
        catch (Exception)
        {
            // 追加のプラグインが見えなくても、既存のプロファイル（CPU の復号など）で動き続けられるようにする。
        }
    }

    /// <summary>共有 resolver からの呼び出し。該当 DLL でなければ IntPtr.Zero。</summary>
    public static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!GstNative.IsGstLibrary(libraryName))
            return IntPtr.Zero;

        GstRoot? root = FindGstRoot();
        if (root is null)
            return IntPtr.Zero;

        string gstBin = System.IO.Path.Combine(root.Value.Path, "bin");
        string self = System.IO.Path.Combine(AppContext.BaseDirectory, GstNative.Lib);
        if (!System.IO.File.Exists(self))
            return IntPtr.Zero;

        if (root.Value.Source == GstRootSource.Bundled)
            ApplyBundledPluginEnvironment(root.Value.Path);
        else
            ApplyExtraPluginEnvironment(root.Value.Source);

        bool originalApplied = SetDllDirectory(gstBin);
        try
        {
            return NativeLibrary.Load(self, assembly, searchPath);
        }
        catch (DllNotFoundException)
        {
            return IntPtr.Zero;
        }
        finally
        {
            // 呼び出し元(App)が設定した BaseDirectory 検索を復元する。
            // ただし同梱ランタイムでは、後から読み込まれるプラグインの依存 DLL
            // （avcodec や gstaudio 等。dist\gstreamer\lib ではなく bin にある）も
            // 同梱 bin から解決させる必要があるため、復元せず検索パスに残す
            // （アプリのディレクトリは既定で検索される）。
            if (originalApplied && root.Value.Source != GstRootSource.Bundled)
                SetDllDirectory(AppContext.BaseDirectory);
        }
    }
}
