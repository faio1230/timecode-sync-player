using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TimecodeSyncPlayer.Gst;

internal sealed class GstNativeApi : IGstNativeApi
{
    private const int ErrBufSize = 512;
    private const int PathBufSize = 4096;

    public IntPtr PlayerCreate(string senderName, out string error)
        => PlayerCreate(senderName, IntPtr.Zero, out error);

    public IntPtr PlayerCreate(string senderName, IntPtr externalDevice, out string error)
    {
        var err = new byte[ErrBufSize];
        IntPtr p = GstNative.Imports.tcs_player_create(senderName, externalDevice, err, (UIntPtr)err.Length);
        error = DecodeUtf8Z(err);
        return p;
    }

    public void PlayerDestroy(IntPtr player) => GstNative.Imports.tcs_player_destroy(player);

    public int Load(IntPtr player, string path, double startSeconds, bool paused, out string error)
    {
        byte[] pathUtf8 = Encoding.UTF8.GetBytes(path + "\0");
        var err = new byte[ErrBufSize];
        int rc = GstNative.Imports.tcs_player_load(player, pathUtf8, startSeconds, paused ? 1 : 0, err, (UIntPtr)err.Length);
        error = DecodeUtf8Z(err);
        return rc;
    }

    public int Stop(IntPtr player) => GstNative.Imports.tcs_player_stop(player);

    public int SetPaused(IntPtr player, bool paused) =>
        GstNative.Imports.tcs_player_set_paused(player, paused ? 1 : 0);

    public bool IsPaused(IntPtr player) => GstNative.Imports.tcs_player_get_paused(player) == 1;

    public ulong Seek(IntPtr player, double seconds) => GstNative.Imports.tcs_player_seek(player, seconds);

    public ulong StepFrame(IntPtr player) => GstNative.Imports.tcs_player_step_frame(player);

    public ulong GetGeneration(IntPtr player) => GstNative.Imports.tcs_player_get_generation(player);

    public ulong SetGeneration(IntPtr player, ulong generation) =>
        GstNative.Imports.tcs_player_set_generation(player, generation);

    public int SetSpeed(IntPtr player, double rate) => GstNative.Imports.tcs_player_set_speed(player, rate);

    public int SetVolume(IntPtr player, double volume0To100) =>
        GstNative.Imports.tcs_player_set_volume(player, volume0To100);

    public int SetMute(IntPtr player, bool mute) => GstNative.Imports.tcs_player_set_mute(player, mute ? 1 : 0);

    public bool TryGetTimePos(IntPtr player, out double seconds) =>
        GstNative.Imports.tcs_player_get_time_pos(player, out seconds) == 0;

    public bool TryGetDuration(IntPtr player, out double seconds) =>
        GstNative.Imports.tcs_player_get_duration(player, out seconds) == 0;

    public bool TryGetFps(IntPtr player, out double fps) =>
        GstNative.Imports.tcs_player_get_fps(player, out fps) == 0;

    public string GetPath(IntPtr player)
    {
        var buf = new byte[PathBufSize];
        return GstNative.Imports.tcs_player_get_path(player, buf, (UIntPtr)buf.Length) == 0
            ? DecodeUtf8Z(buf)
            : string.Empty;
    }

    public bool TryGetSize(IntPtr player, out int width, out int height) =>
        GstNative.Imports.tcs_player_get_size(player, out width, out height) == 0;

    public void SetFrameCallback(IntPtr player, GstNative.TcsFrameNotifyDelegate? callback) =>
        GstNative.Imports.tcs_player_set_frame_callback(player, callback, IntPtr.Zero);

    public int ConsumeUpdate(IntPtr player) => GstNative.Imports.tcs_player_consume_update(player);

    public int Acquire(IntPtr player, ulong generation, out GstNative.TcsFrameInfo info) =>
        GstNative.Imports.tcs_player_acquire(player, generation, out info);

    public bool TryGetLeasedTexture(IntPtr player, out IntPtr texture, out uint subresource, out uint dxgiFormat) =>
        GstNative.Imports.tcs_player_leased_texture(player, out texture, out subresource, out dxgiFormat) == 0;

    public int LeasedCpuCopy(IntPtr player, IntPtr dst, int dstStride) =>
        GstNative.Imports.tcs_player_leased_cpu_copy(player, dst, dstStride);

    public void Release(IntPtr player) => GstNative.Imports.tcs_player_release(player);

    public int PublishSpoutVerification(IntPtr player) => GstNative.Imports.tcs_player_publish_spout(player);

    public int SendImage(IntPtr player, IntPtr bgra, int width, int height, int pitch) =>
        GstNative.Imports.tcs_player_send_image(player, bgra, width, height, pitch);

    public string DecoderName(IntPtr player)
    {
        var buf = new byte[128];
        return GstNative.Imports.tcs_player_decoder_name(player, buf, (UIntPtr)buf.Length) == 0
            ? DecodeUtf8Z(buf)
            : string.Empty;
    }

    public bool SpoutReady(IntPtr player) => GstNative.Imports.tcs_player_spout_ready(player) == 1;

    public int DrainDeliveryEvents(IntPtr player, GstNative.TcsDeliveryEvent[] buffer, uint capacity, out uint count)
        => GstNative.Imports.tcs_player_drain_delivery_events(player, buffer, capacity, out count);

    public int GetDeliveryStats(IntPtr player, out GstNative.TcsDeliveryStats stats)
        => GstNative.Imports.tcs_player_get_delivery_stats(player, out stats);

    public int GetRingInfo(IntPtr player, IntPtr[] handles, uint capacity, out uint count,
        out IntPtr fence, out int width, out int height)
        => GstNative.Imports.tcs_player_ring_info(player, handles, capacity, out count, out fence, out width, out height);

    private static string DecodeUtf8Z(byte[] buf)
    {
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = buf.Length;
        return Encoding.UTF8.GetString(buf, 0, len);
    }
}
