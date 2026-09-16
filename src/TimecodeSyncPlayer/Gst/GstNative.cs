using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// tcs_gstreamer.dll 縺ｮ P/Invoke 縺ｨ繝ｭ繝ｼ繝芽ｧ｣豎ｺ縲・/// DLL 閾ｪ菴薙・繧｢繝励Μ蜃ｺ蜉帙ョ繧｣繝ｬ繧ｯ繝医Μ縲；Streamer 螳溯｡梧凾 DLL 縺ｯ
/// GSTREAMER_1_0_ROOT_MSVC_X86_64・医∪縺溘・譌｢螳壹う繝ｳ繧ｹ繝医・繝ｫ蜈茨ｼ峨・ bin 縺九ｉ隗｣豎ｺ縺吶ｋ縲・/// </summary>
internal static class GstNative
{
    internal const string Lib = "tcs_gstreamer.dll";
    internal const int TcsErrEnded = -6;
    /* tcs_gstreamer.h の tcs_decode_mode と同じ値。 */
    internal const int DecodeModeHardware = 0;
    internal const int DecodeModeSoftware = 1;

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
        internal static extern int tcs_player_get_time_pos(IntPtr player, out double outSec);

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
        internal static extern int tcs_player_leased_cpu_copy(IntPtr player, IntPtr dst, int dstStride);

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
    /// 同梱ランタイムを使うときだけ、プラグイン探索とレジストリキャッシュを同梱ディレクトリへ固定する。
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
