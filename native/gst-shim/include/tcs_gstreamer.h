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
#define TCS_ERR_ENDED -6   /* acquire: end of stream, nothing left to hand over */
#define TCS_ERR_INVALID_ARG -7

/* Decode mode for tcs_player_set_decode_mode. HARDWARE is 0 so a caller that
 * never calls the setter keeps the hardware-first order (the default). */
typedef enum tcs_decode_mode {
  TCS_DECODE_MODE_HARDWARE = 0,  /* GPU profiles first (default) */
  TCS_DECODE_MODE_SOFTWARE = 1   /* CPU profiles first; GPU only as a last resort */
} tcs_decode_mode;

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
  int32_t slot;          /* stage 6b: shared ring slot 0..2; -1 = legacy sample lease */
  uint32_t ring_epoch;   /* D8: epoch of the ring this slot belongs to; 0 = no ring lease */
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

/* 0.4.5-C: long-GOP warning state, safe to poll from the owner thread.
 * active=0 means "no GOP probe" (not loaded, decodebin fallback, or the
 * probe was torn down): the owner must not read that as "no problem".
 * median_interval_sec = 0 while the interval is unconfirmed. The judgement is
 * the median of the measured keyframe intervals (0.4.5-C2); the warning
 * latches once at least two intervals are known and never clears before the
 * next load. */
typedef struct TcsGopStatus {
  int32_t  state;              /* 0 = measuring, 1 = warning (latched) */
  int32_t  active;             /* 1 = a video chain with the GOP probe is built */
  uint64_t keyframes;          /* keyframes observed since load */
  double   median_interval_sec;/* median keyframe interval (0 = unconfirmed) */
  double   pending_sec;        /* current buffer PTS - anchor PTS */
  double   threshold_sec;      /* effective threshold */
  uint64_t warning_qpc;        /* QPC when latched (0 = none) */
} TcsGopStatus;

/* ---- delivery trace (problem H instrumentation) ----
 * Every on_new_sample arrival appends one event. qpc is the same
 * QueryPerformanceCounter clock the compositor uses, so the owner can
 * merge these with its events.jsonl timeline. The ring keeps the newest
 * events; the owner drains it periodically and never blocks the stream. */
typedef struct TcsDeliveryEvent {
  uint64_t qpc;          /* QPC at on_new_sample entry */
  uint64_t seq;          /* shim frame sequence */
  int64_t  pts_ns;
  int64_t  running_ns;   /* segment running time at pts */
  uint32_t callback_us;  /* frame-notify callback duration */
  uint32_t flags;        /* 1=replaced previous latest, 2=callback, 4=gpu,
                          * 8=position snapshot (tcs_player_get_time_pos):
                          * running_ns=queried position, pts_ns/seq=newest
                          * delivered frame at the same instant; only emitted
                          * while the output trace is enabled,
                          * 16=the position snapshot took the delivered-PTS
                          * fallback (pipeline query failed): running_ns=the
                          * returned fallback value (= pts_ns),
                          * 32=the fallback was rejected because the newest
                          * delivered frame belongs to an older generation
                          * (running_ns=0, pts_ns=the stale PTS); that query
                          * returned TCS_ERR_NOT_LOADED */
} TcsDeliveryEvent;

typedef struct TcsDeliveryStats {
  uint64_t arrivals;
  uint64_t latest_replaced;
  uint64_t qos_events;    /* QoS events seen on the appsink sink pad */
  uint64_t decoder_out;   /* buffers leaving the video decoder */
  uint64_t ring_dropped;  /* delivery events evicted before the owner drained */
  uint64_t last_qpc;
} TcsDeliveryStats;

TCS_GST_API int tcs_player_drain_delivery_events(TcsPlayer* player,
                                                 TcsDeliveryEvent* out,
                                                 uint32_t capacity,
                                                 uint32_t* out_count);
TCS_GST_API int tcs_player_get_delivery_stats(TcsPlayer* player,
                                              TcsDeliveryStats* out);

/* ---- shared texture ring (stage 6b: separate decoder device) ----
 * The shim creates its own ID3D11Device on the adapter LUID supplied at
 * create() time and never touches the compositor device/context. Decoded GPU
 * frames are copied into a 3-slot ring of NT-shared BGRA textures (same video
 * dimensions as the negotiated caps) and published with a shared fence whose
 * value is the frame seq (see TcsFrameInfo.seq). The compositor opens the
 * handles once, then waits for `seq` on the GPU queue (ID3D11DeviceContext4)
 * before drawing. acquire() reports the slot through TcsFrameInfo.slot.
 *
 * D8: the ring follows the frame dimensions. When a GPU frame with different
 * dimensions arrives, the shim rebuilds the ring (handles and fence are new)
 * and increments the ring epoch. The compositor compares
 * TcsFrameInfo.ring_epoch with tcs_player_ring_epoch() and reopens the handles
 * when it changed; opened handles keep the old ring allocations alive until
 * the compositor releases them.
 * Handles are owned by the shim: do NOT CloseHandle them, they stay valid
 * until tcs_player_destroy. Returns TCS_ERR_NO_FRAME before the first GPU
 * frame (or on the CPU path) and TCS_ERR_SIZE when capacity < the ring size. */
TCS_GST_API int tcs_player_ring_info(TcsPlayer* player, void** out_handles,
                                     uint32_t capacity, uint32_t* out_count,
                                     void** out_fence, uint32_t* out_width,
                                     uint32_t* out_height);

/* D8: current shared-ring epoch (0 = no ring). Incremented on every
 * (re)build; the compositor reopens the ring when the epoch in
 * TcsFrameInfo.ring_epoch differs from this value. */
TCS_GST_API int tcs_player_ring_epoch(TcsPlayer* player, uint32_t* out_epoch);

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

/* T5: apply a playback-rate change WITHOUT flushing the pipeline
 * (GStreamer 1.18+ GST_SEEK_FLAG_INSTANT_RATE_CHANGE).
 *
 * Contract:
 *  - No flush and no position change: the pipeline keeps playing, the frame
 *    supply is NOT interrupted and the generation is not bumped.
 *  - Not available below GStreamer 1.18 (returns TCS_ERR_GENERIC there).
 *  - rate must be finite and > 0. Direction changes are not supported.
 *  - Refuses while the player is paused (a non-flushing seek in PAUSED is
 *    undefined); the caller must only use this while playing.
 *  - Returns TCS_OK when the seek event was accepted, TCS_ERR_GENERIC when
 *    the pipeline refused the flag or the rate is invalid, and
 *    TCS_ERR_NOT_LOADED when no pipeline is loaded.
 *  - A caller that gets an error must stop using smooth rate correction and
 *    report it; do NOT fall back to a flushing seek per call (that would
 *    reintroduce the picture jump this path exists to avoid).
 *  - tcs_player_set_speed (the flushing path) is unchanged and remains the
 *    manual speed / jump path. */
TCS_GST_API int tcs_player_set_rate_instant(TcsPlayer* player, double rate);
TCS_GST_API int tcs_player_set_volume(TcsPlayer* player, double volume_0_to_100);
TCS_GST_API int tcs_player_set_mute(TcsPlayer* player, int mute);
/* Select the decode mode used by subsequent tcs_player_load calls. Must be
 * called before the first load (the pipeline and the last-good profile cache
 * belong to the mode they were built with); a later call returns
 * TCS_ERR_GENERIC and leaves the current mode unchanged. Call it from the
 * same control thread that calls load/seek/pause. Returns TCS_OK,
 * TCS_ERR_INVALID_ARG for an unknown mode, or TCS_ERR_GENERIC (NULL player or
 * already loaded). In software mode a load that ends on a GPU profile logs a
 * warning ("no CPU decoder was usable"). */
TCS_GST_API int tcs_player_set_decode_mode(TcsPlayer* player, int mode);

/* Media-time queries (seconds). TCS_OK or TCS_ERR_NOT_LOADED. */
TCS_GST_API int tcs_player_get_time_pos(TcsPlayer* player, double* out_sec);

/* 0.4.5-A: which clock the returned position is on. */
#define TCS_POSITION_BASIS_NONE      0
#define TCS_POSITION_BASIS_PIPELINE  1  /* gst_element_query_position */
#define TCS_POSITION_BASIS_DELIVERED 2  /* newest delivered video frame stream PTS */

/* One coherent snapshot (taken under the player's frame lock):
 *   seconds            same value as tcs_player_get_time_pos
 *   basis              TCS_POSITION_BASIS_* of `seconds`
 *   generation         generation `seconds` belongs to
 *   delivered_seconds  newest delivered video frame PTS (0 = none)
 *   delivered_generation  generation of delivered_seconds (0 = none)
 *   current_generation player generation at the same instant
 * The delivered_* fields are filled on every TCS_OK path, so the owner can
 * compare the delivered clock with the current generation while playing.
 * TCS_OK, TCS_ERR_GENERIC (bad args) or TCS_ERR_NOT_LOADED (no pipeline). */
typedef struct TcsPositionSample {
  double   seconds;
  int32_t  basis;
  uint64_t generation;
  double   delivered_seconds;
  uint64_t delivered_generation;
  uint64_t current_generation;
} TcsPositionSample;

TCS_GST_API int tcs_player_get_time_pos_ex(TcsPlayer* player, TcsPositionSample* out);
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
TCS_GST_API void tcs_player_release(TcsPlayer* player);

/* ---- verification layer (prototype/Spout receiver tests only) ---- */
TCS_GST_API int tcs_player_publish_spout(TcsPlayer* player);
TCS_GST_API int tcs_player_send_image(TcsPlayer* player, const uint8_t* bgra,
                                      int width, int height, int pitch);

TCS_GST_API int tcs_player_get_stats(TcsPlayer* player, TcsStats* out);
/* 0.4.5-C: snapshot of the long-GOP detector. Takes no lock the streaming
 * thread needs (dedicated gop lock only). TCS_OK or TCS_ERR_GENERIC. */
TCS_GST_API int tcs_player_get_gop_status(TcsPlayer* player, TcsGopStatus* out);

/* ---- 0.4.5-C3: static keyframe scan (no player, no decoding) ----
 * Reads the container with filesrc ! parsebin ! fakesink and records the PTS of
 * every non-DELTA_UNIT buffer. Nothing is decoded, so 4K material scans at I/O
 * speed. The scan has its own pipeline and shares no state with any TcsPlayer,
 * so it never touches the playback/seek state machine.
 *
 * The judgement uses the MAXIMUM gap: material whose median sits inside the
 * recommendation can still hold a 7s gap, and a seek landing there is slow
 * (measured: 244ms right after a keyframe, 2164ms just before the next one on
 * a 6.6s gap). head_gap (0 -> first keyframe) and tail_gap (last keyframe ->
 * duration) are part of the gap list for the same reason. */
typedef struct TcsGopScan {
  int32_t  keyframes;          /* keyframes found (0 = scan produced nothing) */
  int32_t  truncated;          /* 1 = stopped on the time budget; the gaps are a LOWER bound */
  double   duration_sec;       /* container duration (0 = unknown) */
  double   head_gap_sec;       /* 0 -> first keyframe */
  double   tail_gap_sec;       /* last keyframe -> duration */
  double   median_gap_sec;
  double   p95_gap_sec;
  double   max_gap_sec;        /* the value the judgement uses */
} TcsGopScan;

/* Returns TCS_OK whether the scan completed or hit the budget; `truncated` says
 * which. The call blocks for the length of the scan (I/O bound: ~1.2 GB/s
 * measured), so call it off the UI thread.
 *
 * budget_ms caps the work. Field material runs to 50GB, and all-intra ProRes
 * masters reach 200-300GB; reading those end to end would take minutes, and for
 * all-intra material the answer is obvious within the first seconds. On a
 * truncated scan the caller must treat the gaps as a LOWER bound: a long gap
 * that was found is real, but "no long gap" only covers the part that was read.
 * budget_ms <= 0 uses 10000. */
TCS_GST_API int tcs_scan_gop(const char* utf8_path, int32_t budget_ms, TcsGopScan* out);
/* Convenience getters (avoid struct marshalling from .NET). */
TCS_GST_API int tcs_player_decoder_name(TcsPlayer* player, char* out, size_t out_len);
TCS_GST_API int tcs_player_spout_ready(TcsPlayer* player);

#ifdef __cplusplus
}
#endif

#endif /* TCS_GSTREAMER_H */
