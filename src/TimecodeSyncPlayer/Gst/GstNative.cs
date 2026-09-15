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
    internal static extern int tcs_player_set_rate_instant(IntPtr player, double rate);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_volume(IntPtr player, double volume0To100);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int tcs_player_set_mute(IntPtr player, int mute);

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
    }
}

/// <summary>

/// <summary>
/// tcs_gstreamer.dll と GStreamer ランタイム DLL の検索経路。
/// アセンブリ共通の DllImportResolver は MpvNativeLibraryResolver が所有するため、
/// ここでは判定と個別ロードだけを提供する。
/// </summary>
internal static class GstNativeLibraryResolver
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    public static string? FindGstBinDirectory()
    {
        string? root = Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MSVC_X86_64");
        if (IsGstRoot(root))
            return System.IO.Path.Combine(root!, "bin");

        root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "gstreamer", "1.0", "msvc_x86_64");
        return IsGstRoot(root) ? System.IO.Path.Combine(root, "bin") : null;

        static bool IsGstRoot(string? dir) =>
            !string.IsNullOrEmpty(dir) &&
            (System.IO.File.Exists(System.IO.Path.Combine(dir!, "bin", "gstreamer-1.0-0.dll"))
             || System.IO.File.Exists(System.IO.Path.Combine(dir!, "bin", "gstreamer-1.0.dll")));
    }

    /// <summary>共有 resolver からの呼び出し。該当 DLL でなければ IntPtr.Zero。</summary>
    public static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!GstNative.IsGstLibrary(libraryName))
            return IntPtr.Zero;

        string? gstBin = FindGstBinDirectory();
        if (gstBin is null)
            return IntPtr.Zero;

        string self = System.IO.Path.Combine(AppContext.BaseDirectory, GstNative.Lib);
        if (!System.IO.File.Exists(self))
            return IntPtr.Zero;

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
            if (originalApplied)
                SetDllDirectory(AppContext.BaseDirectory);
        }
    }
}
