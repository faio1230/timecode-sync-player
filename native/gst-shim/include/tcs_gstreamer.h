/* tcs_gstreamer: GStreamer -> D3D11 frame SOURCE with a C ABI.
 *
 * Role in the product: this shim is the GPU frame SOURCE feeding the
 * composition layer (see docs/OUTPUT-GPU-CONTRACT-MAPPING-2026-09-10.md,
 * "output side finalized version"). Frames are decoded on the GPU,
 * color-converted on the GPU and offered to the compositor as D3D11
 * textures (BGRA, or NV12 if the converter is skipped later) on a device
 * the caller provides, so no cross-device sharing is required.
 *
 * Contract (source side):
 *  - every frame carries (generation, pts, seq): the owner bumps the
 *    generation on load/seek; acquire(gen) NEVER returns a frame of a
 *    different (older) generation;
 *  - acquired frames are LEASED: the underlying decoder/converter pool
 *    texture is not reused until tcs_release() (the pool is finite);
 *  - when nothing suitable exists, acquire returns 0 ("none"). The source
 *    never synthesizes black or error images; holding the last image is the
 *    compositor's job.
 *
 * The Spout entry points below are VERIFICATION ONLY (prototype/receiver
 * tests). Production Spout output publishes the composited image from the
 * composition layer, not from this shim.
 *
 * License: MIT (this file and shim code). GStreamer libraries are LGPL and
 * dynamically linked; Spout SDK sources (BSD-2-Clause) are compiled in.
 */
#ifndef TCS_GSTREAMER_H
#define TCS_GSTREAMER_H

#include <stdint.h>
#include <stddef.h>

#ifdef TCS_GST_BUILDING_DLL
#define TCS_GST_API __declspec(dllexport)
#else
#define TCS_GST_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct TcsPlayer TcsPlayer;

#define TCS_OK 0
#define TCS_ERR_GENERIC -1
#define TCS_ERR_NOT_LOADED -2
#define TCS_ERR_NO_FRAME -3
#define TCS_ERR_SPOUT -4
#define TCS_ERR_SIZE -5

/* Frame notification; called from a GStreamer streaming thread.
 * Mirrors mpv_render_update_fn's "wakeup, go look" semantics while
 * carrying the generation/seq so the owner can gate stale frames. */
typedef void (*tcs_frame_notify_fn)(void* user_data, uint64_t generation,
                                    uint64_t seq);

/* Metadata for one leased frame. */
typedef struct TcsFrameInfo {
  uint64_t generation;
  uint64_t seq;
  int64_t pts_ns;
  int32_t width;
  int32_t height;
  int32_t is_gpu;        /* 1: D3D11 texture; 0: system-memory buffer */
} TcsFrameInfo;

typedef struct TcsStats {
  char decoder[64];
  char last_error[256];
  int32_t gpu_path;
  int32_t eos;
  int32_t error;
  int32_t width;
  int32_t height;
  uint64_t frames_decoded;
  uint64_t spout_sends;   /* verification layer counter */
  uint64_t generation;
} TcsStats;

/* Create the player.
 * external_d3d11_device: ID3D11Device* to share with the compositor;
 *   must have been created with D3D11_CREATE_DEVICE_VIDEO_SUPPORT |
 *   D3D11_CREATE_DEVICE_BGRA_SUPPORT. NULL = create one internally.
 * Returns NULL on failure with a message in errbuf. */
TCS_GST_API TcsPlayer* tcs_player_create(const char* sender_name,
                                         void* external_d3d11_device,
                                         char* errbuf, size_t errbuf_len);
TCS_GST_API void tcs_player_destroy(TcsPlayer* player);

/* Synchronously build a pipeline, preroll one frame. start_sec<0 = 0. */
TCS_GST_API int tcs_player_load(TcsPlayer* player, const char* utf8_path,
                                double start_sec, int paused,
                                char* errbuf, size_t errbuf_len);
TCS_GST_API int tcs_player_stop(TcsPlayer* player);

TCS_GST_API int tcs_player_set_paused(TcsPlayer* player, int paused);
TCS_GST_API int tcs_player_get_paused(TcsPlayer* player);
/* Bump the generation on a manual seek (the owner decides what counts as a
 * new source/seek generation). */
TCS_GST_API uint64_t tcs_player_seek(TcsPlayer* player, double seconds);
TCS_GST_API uint64_t tcs_player_step_frame(TcsPlayer* player);
TCS_GST_API uint64_t tcs_player_set_generation(TcsPlayer* player,
                                               uint64_t generation);
TCS_GST_API uint64_t tcs_player_get_generation(TcsPlayer* player);
TCS_GST_API int tcs_player_set_speed(TcsPlayer* player, double rate);
TCS_GST_API int tcs_player_set_volume(TcsPlayer* player, double volume_0_to_100);
TCS_GST_API int tcs_player_set_mute(TcsPlayer* player, int mute);

/* Media-time queries (seconds). TCS_OK or TCS_ERR_NOT_LOADED. */
TCS_GST_API int tcs_player_get_time_pos(TcsPlayer* player, double* out_sec);
TCS_GST_API int tcs_player_get_duration(TcsPlayer* player, double* out_sec);
TCS_GST_API int tcs_player_get_fps(TcsPlayer* player, double* out_fps);
TCS_GST_API int tcs_player_get_path(TcsPlayer* player, char* out, size_t out_len);
TCS_GST_API int tcs_player_get_size(TcsPlayer* player, int* out_w, int* out_h);

/* Frame notification (streaming thread). Set both NULL to unregister. */
TCS_GST_API void tcs_player_set_frame_callback(TcsPlayer* player,
                                               tcs_frame_notify_fn fn,
                                               void* user_data);
/* 1 when a frame newer than the last consume arrived (clears the flag).
 * Mirrors MPV_RENDER_UPDATE_FRAME for the compatibility adapter. */
TCS_GST_API int tcs_player_consume_update(TcsPlayer* player);

/* ---- LEASE API (the production frame-supply surface) ----
 * acquire: takes the newest frame of generation `generation` not yet
 * delivered. Returns 1 and fills *out_info when a frame was leased, 0 when
 * there is nothing to hand over (never a synthesized placeholder).
 * While a lease is held its pool texture is not reused by the decoder. */
TCS_GST_API int tcs_player_acquire(TcsPlayer* player, uint64_t generation,
                                   TcsFrameInfo* out_info);
/* Texture of the current lease (same device as the one passed to create;
 * no keyed mutex needed in-process). Returns 0 on success. *out_texture is
 * valid until tcs_player_release() and must not be kept past it. */
TCS_GST_API int tcs_player_leased_texture(TcsPlayer* player,
                                          void** out_texture,
                                          uint32_t* out_subresource,
                                          uint32_t* out_dxgi_format);
/* CPU access to the current lease, row-copied into dst (BGRA, dst_stride).
 * This IS a GPU readback: use for preview/debug only, not for Spout. */
TCS_GST_API int tcs_player_leased_cpu_copy(TcsPlayer* player, uint8_t* dst,
                                           int dst_stride);
TCS_GST_API void tcs_player_release(TcsPlayer* player);

/* ---- verification layer (prototype/Spout receiver tests only) ---- */
TCS_GST_API int tcs_player_publish_spout(TcsPlayer* player);
TCS_GST_API int tcs_player_send_image(TcsPlayer* player, const uint8_t* bgra,
                                      int width, int height, int pitch);

TCS_GST_API int tcs_player_get_stats(TcsPlayer* player, TcsStats* out);

#ifdef __cplusplus
}
#endif

#endif /* TCS_GSTREAMER_H */
