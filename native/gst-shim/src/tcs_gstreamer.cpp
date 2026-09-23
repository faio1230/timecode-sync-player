/* tcs_gstreamer implementation (v3). See tcs_gstreamer.h for the contract.
 *
 * Role: GPU frame SOURCE for the compositing layer.
 *   filesrc ! typefind ! demux ! <explicit per-codec chain> ! appsink
 * - decodebin is NOT used for video as a decoder (it would auto-pick CPU
 *   decoders and hand raw pads). Codecs are classified explicitly with GPU
 *   profiles first and CPU fallbacks; CPU decodes are uploaded with
 *   d3d11upload so every lease still goes through the shared ring. Unmatched
 *   video/* falls back to decodebin(sysmem) + d3d11upload. video/x-hap is
 *   refused by design: it must get a dedicated compressed-texture branch
 *   later and must never be auto-decoded by avdec_hap.
 * - The D3D11 device can be supplied by the caller (compositing layer) so
 *   decode, conversion and composition share one device. Frames are offered
 *   to the compositor through a generation-stamped lease API:
 *     acquire(gen) takes the newest frame matching gen as a lease (holding
 *     the GStreamer sample ref keeps its pool texture from reuse), release()
 *     returns it. Nothing is synthesized: when no matching frame exists,
 *     acquire reports "none".
 *   Spout entry points are kept for verification only; the production Spout
 *   output belongs to the composition layer.
 *
 * Threading:
 *   - pipeline control (load/stop/seek) from the single UI-thread caller;
 *   - appsink callback (streaming thread) touches only the frame slots;
 *   - bus thread polls error/EOS.
 *
 * License: MIT. Dynamically links LGPL GStreamer libraries; Spout SDK
 * (BSD-2-Clause) sources are compiled in.
 */

#include "tcs_gstreamer.h"
#include "tcs_delivery_policy.h"
#include "tcs_decode_policy.h"
#include "tcs_gop_policy.h"
#include "tcs_load_policy.h"
#include "tcs_position_policy.h"
#include "tcs_time_mapping.h"
#include "tcs_video_profiles.h"

#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>

#include <gst/gst.h>
#include <gst/app/gstappsink.h>
#include "tcs_hap.h"
#include "tcs_hap_gpu.h"
#include <gst/video/video.h>
#include <gst/d3d11/gstd3d11.h>

#include "SpoutDX.h"

#include <mutex>
#include <string>
#include <vector>
#include <algorithm>
#include <atomic>
#include <thread>
#include <deque>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <cmath>

/* O1: LOG writes to stderr and, when TCS_LOG_FILE names a path, appends the
 * same line to that file (flushed per line). The open is lazy; failures are
 * ignored. The log volume still follows TCS_LEASE_LOG / TCS_FRAME_LOG. */
static void
log_sink_write (const char* line)
{
  fputs (line, stderr);
  static std::mutex log_file_mutex;
  static FILE* log_file = nullptr;
  static bool log_file_tried = false;
  std::lock_guard<std::mutex> g (log_file_mutex);
  if (!log_file_tried) {
    log_file_tried = true;
    char path[1024];
    DWORD n = GetEnvironmentVariableA ("TCS_LOG_FILE", path, sizeof (path));
    if (n > 0 && n < sizeof (path))
      log_file = fopen (path, "a");
  }
  if (log_file) {
    fputs (line, log_file);
    fflush (log_file);
  }
}

#define LOG(fmt, ...) \
  do { \
    char tcs_log_line[2048]; \
    snprintf (tcs_log_line, sizeof (tcs_log_line), "[tcs-gst] " fmt "\n", ##__VA_ARGS__); \
    log_sink_write (tcs_log_line); \
  } while (0)

static std::once_flag g_gst_once;

/* S4 load-latency instrumentation: one QPC clock for every phase. */
static uint64_t
qpc_now (void)
{
  LARGE_INTEGER v;
  QueryPerformanceCounter (&v);
  return (uint64_t) v.QuadPart;
}

static double
qpc_diff_ms (uint64_t from, uint64_t to, int64_t freq)
{
  double f = freq > 0 ? (double) freq : 10000000.0;
  return (double) (to - from) * 1000.0 / f;
}

static bool
env_flag (const char* name)
{
  char buf[8];
  return GetEnvironmentVariableA (name, buf, sizeof (buf)) > 0;
}

static int
env_int (const char* name, int fallback)
{
  char buf[32];
  DWORD n = GetEnvironmentVariableA (name, buf, sizeof (buf));
  if (n == 0 || n >= sizeof (buf))
    return fallback;
  return atoi (buf);
}

/* 0.4.5-C2: keyframe-interval threshold for the long-GOP warning. The site
 * recommendation is a 1..2 s GOP; the judgement is the median measured
 * interval, so 2.0 s means "the typical GOP is longer than the recommendation"
 * (C2: the old 3.0 s keyframe-absence rule fired on variable-GOP material).
 * Only used to initialize the tracker (reset keeps it). */
static const double kGopWarnSecondsDefault = 2.0;

static double
resolve_gop_warn_seconds (void)
{
  char buf[32];
  DWORD n = GetEnvironmentVariableA ("TCS_GOP_WARN_SECONDS", buf, sizeof (buf));
  if (n == 0 || n >= sizeof (buf))
    return kGopWarnSecondsDefault;
  char* end = nullptr;
  double value = strtod (buf, &end);
  if (end == buf || !(value > 0.0) || !(value < 3600.0))
    {
      LOG ("gop warn: invalid TCS_GOP_WARN_SECONDS '%s' -> %.1f",
          buf, kGopWarnSecondsDefault);
      return kGopWarnSecondsDefault;
    }
  return value;
}

/* diagnostics: dump every delivered sample (pacing analysis) */
static bool frame_log = env_flag ("TCS_FRAME_LOG");

/* D10: log once when the segment is unavailable and reported positions fall
 * back to the raw buffer PTS. */
static std::atomic<bool> stream_time_fallback_logged{false};

/* D8 diagnostics: log lease acquire/release and the ring dimension fallback.
 * Off by default; the D8 measurement sets TCS_LEASE_LOG=1. Logs are emitted
 * with no lock held (state changes are recorded under frame_lock and printed
 * after release). */
static bool lease_log = env_flag ("TCS_LEASE_LOG");

/* The owner enables the output trace (events.jsonl) by setting the app's
 * TIMECODE_SYNC_PLAYER_OUTPUT_TRACE. tcs_player_get_time_pos appends a
 * position snapshot to the delivery ring only when that variable was set at
 * create time: with the trace off the default path must not pay for a QPC
 * read or a ring write. */
static const char* kOutputTraceEnv = "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE";

/* TcsDeliveryEvent.flags bit 3: not a frame arrival but the snapshot
 * tcs_player_get_time_pos records while the output trace is enabled.
 * Bit 4 marks the snapshot taken by the delivered-PTS fallback (query failed);
 * bit 5 marks a fallback that was rejected because the newest delivered frame
 * belongs to an older generation (0.4.5-A phase 2). */
enum { kDeliveryFlagPosition = 8, kDeliveryFlagPositionFallback = 16,
       kDeliveryFlagPositionFallbackRejected = 32 };

/* D2 diagnostics: pipeline/sink state around seeks, pause changes and steps.
 * The paused-seek bug is about state, so the report needs the state at each
 * boundary, not just arrival times. */
static bool paused_seek_diag = env_flag ("TCS_PAUSED_SEEK_DIAG");

/* D2 rework test hook: hold frame_lock in on_new_sample for the first few
 * samples, widening the streaming-thread window that a state change under
 * frame_lock deadlocks with. No effect when unset. */
static int test_hold_frame_lock_ms = env_int ("TCS_TEST_HOLD_FRAME_LOCK_MS", 0);
static std::atomic<int> test_hold_budget{test_hold_frame_lock_ms > 0 ? 40 : 0};

/* Same idea for the seek path: hold frame_lock inside seek_locked before the
 * seek event is sent, so on_new_sample queues on frame_lock while the flush
 * seek needs the streaming thread. No effect when unset. */
static int test_hold_seek_lock_ms = env_int ("TCS_TEST_HOLD_SEEK_LOCK_MS", 0);
static std::atomic<int> test_seek_hold_budget{test_hold_seek_lock_ms > 0 ? 8 : 0};

/* C1(b) measurement switch: seek method. auto keeps the container default
 * (tsdemux -> KEY_UNIT|SNAP_BEFORE, others -> ACCURATE). accurate/keyunit
 * force one method for the comparison. Unknown values fall back to auto with
 * a one-time warning. The delivery gate and the method-2 segment rewrite are
 * armed only for the keyunit method (any container), so forcing accurate on
 * TS leaves neither armed. */
enum {
  kSeekMethodAuto = 0,
  kSeekMethodAccurate = 1,
  kSeekMethodKeyUnit = 2,
};

static int
resolve_seek_method (void)
{
  char buf[32];
  DWORD n = GetEnvironmentVariableA ("TCS_SEEK_METHOD", buf, sizeof (buf));
  int method = kSeekMethodAuto;
  if (n > 0 && n < sizeof (buf)) {
    if (_stricmp (buf, "accurate") == 0)
      method = kSeekMethodAccurate;
    else if (_stricmp (buf, "keyunit") == 0)
      method = kSeekMethodKeyUnit;
    else if (_stricmp (buf, "auto") != 0)
      LOG ("seek-method: unknown value '%s' -> auto", buf);
  } else if (n >= sizeof (buf)) {
    LOG ("seek-method: value too long -> auto");
  }
  LOG ("seek-method: %s",
      method == kSeekMethodAccurate ? "accurate" :
      method == kSeekMethodKeyUnit ? "keyunit" : "auto");
  return method;
}

static int seek_method = resolve_seek_method ();

/* D24: the paused-seek pump deadline is only an upper bound. An accurate seek
 * decodes from the previous keyframe up to the target, so a long GOP (e.g. a
 * 10s keyframe interval) legitimately takes longer than the old fixed 500ms.
 * Default 4000ms; TCS_PUMP_BUDGET_MS overrides it (1..60000). */
static const ULONGLONG kPumpBudgetDefaultMs = 4000;
static const ULONGLONG kPumpBudgetMaxMs = 60000;

static ULONGLONG
resolve_pump_budget_ms (void)
{
  char buf[32];
  DWORD n = GetEnvironmentVariableA ("TCS_PUMP_BUDGET_MS", buf, sizeof (buf));
  ULONGLONG ms = kPumpBudgetDefaultMs;
  if (n > 0 && n < sizeof (buf)) {
    long long v = _strtoi64 (buf, nullptr, 10);
    if (v > 0 && (ULONGLONG) v <= kPumpBudgetMaxMs)
      ms = (ULONGLONG) v;
    else
      LOG ("pump-budget: value '%s' out of range -> default %llums",
          buf, (unsigned long long) kPumpBudgetDefaultMs);
  } else if (n >= sizeof (buf)) {
    LOG ("pump-budget: value too long -> default %llums",
        (unsigned long long) kPumpBudgetDefaultMs);
  }
  LOG ("pump-budget: deadline %llums", (unsigned long long) ms);
  return ms;
}

static ULONGLONG pump_budget_ms = resolve_pump_budget_ms ();

struct TcsPlayer {
  /* D3D11 + Spout.
   * Stage 6b: the device is ALWAYS owned by the shim. The compositor pointer
   * passed to create() is only used to read its adapter LUID, then a separate
   * device is created on that adapter (video support). The compositor's
   * immediate context is never touched. */
  ID3D11Device* device = nullptr;
  bool owns_device = false;
  ID3D11DeviceContext* context = nullptr;
  ID3D11Device5* device5 = nullptr;          /* D3D11.4, ring fence creation */
  ID3D11DeviceContext4* context4 = nullptr;  /* D3D11.4, ring fence signal */
  GstD3D11Device* gst_dev = nullptr;      /* wrapped device, ref'd for lifetime */
  spoutDX* spout = nullptr;               /* verification layer only */
  bool spout_ready = false;
  std::string sender_name;

  /* GStreamer (UI-thread structural changes only) */
  GstElement* pipeline = nullptr;
  GstElement* demux = nullptr;
  GstElement* ahead = nullptr;
  GstElement* appsink = nullptr;
  GstElement* vcaps = nullptr;
  GstElement* vhead = nullptr;
  GstElement* vqueue = nullptr;           /* D13: demux-side video queue (decouples demux from appsink preroll) */
  GstElement* vparse = nullptr;
  GstElement* vdec = nullptr;
  GstElement* vconvert = nullptr;
  GstElement* vupload = nullptr;          /* CPU decode: sysmem BGRA -> D3D11 */
  GstElement* vgpuconvert = nullptr;      /* CPU decode: uploaded -> BGRA on the GPU (V11-h) */
  /* v0.5.0: HAP は圧縮テクスチャのまま appsink で受け、shim 内で BGRA へ展開してリングへ載せる。
   * hap_stream はこのファイルが HAP だと分かった時点で立ち、次の試行で HAP の連鎖を組む。 */
  bool hap_stream = false;
  TcsHapGpu* hap_gpu = nullptr;
  std::vector<uint8_t> hap_buffer;        /* 展開先（コマごとに使い回す） */
  uint64_t hap_decode_failures = 0;
  bool vchain_built = false;
  bool capsMismatch = false;
  bool rejected = false;
  int vProfile = 0;
  int lastGoodProfile = 0;
  int decode_mode = TCS_DECODE_MODE_HARDWARE;  /* tcs_player_set_decode_mode */
  bool ever_loaded = false;                    /* the first load locks decode_mode */
  GstElement* aconvert = nullptr;         /* first: accepts non-interleaved decoder output */
  GstElement* aqueue = nullptr;
  GstElement* avolume = nullptr;
  GstElement* aconvert2 = nullptr;        /* second: sink format negotiation */
  GstElement* aresample = nullptr;        /* D12: resample to the device rate (44.1k -> 48k) */
  GstElement* asink = nullptr;
  GstElement* adecodebin = nullptr;
  gboolean use_d3d11_caps = FALSE;
  bool audioEnabled = true;
  bool use_fakesink = false;              /* autoaudiosink unusable -> fakesink sync=true */
  gboolean needAudioDecode = TRUE;

  /* frame slots (frame_lock) */
  std::mutex frame_lock;
  /* Small FIFO of undelivered samples: acquire() pops the oldest of the
   * requested generation. Kept small (problem H: a single "latest" slot
   * lost a frame whenever two arrivals landed inside one compose tick). */
  struct FrameSlot {
    GstSample* sample;
    uint64_t generation;
    uint64_t seq;
    uint64_t pts_ns;
    uint64_t arrival_qpc;                 /* H-3: for the age-based backlog rule */
    bool gpu;
    int32_t slot;                         /* stage 6b: shared ring slot; -1 = sample lease */
    uint32_t ring_epoch;                  /* D8: epoch of the ring holding `slot` (0 = sample) */
  };
  static const uint32_t kFrameQueueCapacity = 4;
  std::deque<FrameSlot> frames;
  int64_t qpc_freq = 10000000;            /* QueryPerformanceFrequency */
  uint64_t latest_gen = 0;                /* newest arrival generation (diagnostics) */
  uint64_t latest_pts_ns = 0;
  uint64_t latest_seq = 0;
  bool latest_gpu = false;
  GstSample* leased = nullptr;            /* held by the compositor */
  int32_t leased_slot = -1;               /* ring slot of the current lease, -1 = sample */
  TcsFrameInfo lease_info = {};
  bool pending_update = false;

  /* shared texture ring (stage 6b, frame_lock). D8: follows the frame
   * dimensions. Rebuilt when a GPU frame with different dimensions arrives;
   * the compositor opens the NT handles via tcs_player_ring_info() and compares
   * the epoch via tcs_player_ring_epoch() to detect a rebuild. */
  static const uint32_t kRingSlots = 3;
  bool ring_ready = false;
  int ring_width = 0;
  int ring_height = 0;
  uint32_t ring_epoch = 0;                /* 0 = no ring; +1 on every build */
  ID3D11Texture2D* ring_texture[kRingSlots] = {};
  HANDLE ring_handle[kRingSlots] = {};
  ID3D11Fence* ring_fence = nullptr;
  HANDLE ring_fence_handle = nullptr;
  uint64_t generation = 1;                /* bumped by owner on load/seek */
  /* D25: downstream SEGMENT events that passed the appsink pad (the pad probe
   * runs on the streaming thread, so its count is an exact pre/post-seek
   * boundary: a flushing seek always produces one new segment before its
   * first buffer). Every buffer is tagged with the count at entry; a sample
   * whose tag is below the boundary expected from the latest seek was pulled
   * before that seek and must not be published under its generation. */
  std::atomic<uint64_t> flush_boundary{0};
  std::atomic<uint64_t> seek_boundary_expect{0};
  std::atomic<ULONGLONG> seek_expect_ms{0};   /* when the expectation was set */
  std::atomic<uint64_t> stale_drops{0};
  std::atomic<bool> flush_marker_missing{false};
  /* D25: fence signal values (lease seq) must never repeat while the shared
   * fence object lives: frames_decoded resets per load, this does not. */
  uint64_t seq_serial = 0;                /* frame_lock */
  /* MPEG-TS precise seek (method 5, guarded by frame_lock): tsdemux's
   * ACCURATE scan loses H.264 NALs when IDRs carry no SPS/PPS, so a TS seek
   * snaps to the keyframe before the target with KEY_UNIT|SNAP_BEFORE and the
   * frames decoded before the target are dropped until the first one at or
   * after it. gate_target_ns is in the same media-time space as the requested
   * seconds; gate_dropped counts the frames dropped for the last seek. */
  bool mpegts = false;                    /* current demux is tsdemux */
  bool gate_active = false;               /* drop samples with pts < target */
  uint64_t gate_target_ns = 0;
  uint64_t gate_dropped = 0;
  uint64_t gate_armed_qpc = 0;            /* diagnostics: arm/first/open times */
  uint64_t gate_first_qpc = 0;
  uint64_t gate_first_pts_ns = 0;
  guint64 frames_decoded = 0;
  guint64 spout_sends = 0;

  /* Method 2 (clock rebase, TS only): the flushing seek restarts the tsdemux
   * segment at the snapped keyframe and rebases base_time so that keyframe is
   * "now"; the target frame would then be waited out in real time. The shim
   * rewrites the post-seek SEGMENT at the sinks (start = target, like
   * qtdemux's accurate seek) so pre-target buffers are dropped before
   * preroll, the pipeline anchors base_time on the target, and audio starts
   * at the target too. MP4/MOV keep the accurate seek and no rewrite. */
  bool rebase_armed = false;              /* waiting for the post-seek segment */
  guint32 rebase_seek_seqnum = 0;         /* seqnum of the last TS seek event */
  bool video_rewrite_installed = false;
  GstElement* audio_rewrite_sink = nullptr; /* probe target (pipeline-owned) */
  uint64_t rebase_count = 0;
  /* TS seek diagnostics: buffers arriving at the audio sink since the last
   * rewritten segment, and a one-shot stall report. */
  std::atomic<uint64_t> au_probe_buffers{0};
  std::atomic<int64_t> au_probe_first_pts{-1};
  std::atomic<int64_t> au_probe_last_pts{-1};
  std::atomic<uint64_t> vp_probe_buffers{0};
  std::atomic<int64_t> vp_probe_first_pts{-1};
  std::atomic<int64_t> vp_probe_last_pts{-1};
  bool gate_diag_done = false;
  uint32_t seg_diag_left = 0;
  /* A/V diagnostics: after each rebase, log the audio sink position next to
   * the delivered video pts for the first few frames, then every Nth. */
  uint32_t av_log_left = 0;
  uint32_t av_log_stride = 0;
  uint64_t av_log_seq = 0;

  /* delivery trace (frame_lock). problem H: record each arrival so the
   * owner can compare decoding cadence, callback cost and replacements. */
  static const uint32_t kDeliveryCapacity = 16384;
  TcsDeliveryEvent delivery_ring[kDeliveryCapacity] = {};
  uint32_t delivery_read = 0;
  uint32_t delivery_write = 0;
  uint64_t delivery_arrivals = 0;
  uint64_t delivery_replaced = 0;
  std::atomic<uint64_t> delivery_qos{0};         /* pad probes must not take frame_lock */
  std::atomic<uint64_t> delivery_decoder_out{0}; /* (preroll waits on the streaming thread) */
  uint64_t delivery_ring_dropped = 0;
  uint64_t delivery_last_qpc = 0;
  bool position_trace = false;            /* output trace was on at create */

  /* callback */
  tcs_frame_notify_fn notify = nullptr;
  void* notify_user = nullptr;

  /* D2: paused-seek preroll pump. A flushing seek on a PAUSED pipeline leaves
   * the target sample in preroll; appsink has sync=true, so new_sample is only
   * emitted once the clock runs. The pump briefly runs the pipeline PLAYING
   * (muted) until a frame of the seek generation is queued, then returns to
   * PAUSED. tcs_player_seek only arms it (non-blocking); the bus thread ticks
   * it; state_mutex serializes the final PAUSED decision with pause calls so a
   * user resume always wins. */
  std::mutex state_mutex;
  std::atomic<bool> pump_pending{false};
  bool pump_active = false;              /* frame_lock */
  uint64_t pump_generation = 0;          /* frame_lock */
  ULONGLONG pump_deadline = 0;           /* frame_lock */
  ULONGLONG pump_armed_ms = 0;           /* frame_lock (D24 diagnostics) */
  uint64_t pump_frames_at_arm = 0;       /* frame_lock (D24 diagnostics) */
  bool pump_muted = false;               /* frame_lock */
  uint64_t pump_faults = 0;              /* frame_lock (diagnostics) */

  /* 0.4.5-C long-GOP detector. gop_lock is a leaf lock: only the pad probe
   * (streaming thread), the getter and teardown's reset take it; it is never
   * held together with frame_lock / state_mutex and no GStreamer call runs
   * under it. gop_epoch invalidates probe contexts of a torn-down pipeline. */
  std::mutex gop_lock;
  TcsGopTracker gop_tracker = {};
  bool gop_active = false;               /* gop_lock: video chain with GOP probe built */
  bool gop_probe_installed = false;      /* gop_lock: at most one probe per pipeline */
  std::atomic<uint64_t> gop_epoch{0};    /* +1 on teardown; probe contexts carry it */
  std::atomic<bool> gop_reanchor{false}; /* seek_prepare_locked -> probe clears it */

  /* audio priming: a paused load must not yank the audio sink down while it
   * is still initializing (wasapi2 stopped mid-init never recovers). */
  std::atomic<uint64_t> audio_sink_buffers{0};
  bool load_priming = false;

  /* stream info (frame_lock) */
  std::string path;
  std::string decoder_name;
  std::string decoder_element_name;       /* D16-b: adapter-matched decoder class for this attempt */
  std::string last_error;
  std::string last_bus_error;             /* O1: last GST_MESSAGE_ERROR text of this load */
  double fps = 0.0;
  double duration = -1.0;
  int width = 0;
  int height = 0;
  double rate = 1.0;
  double volume_value = 100.0;
  bool muted = false;
  bool paused = true;
  bool eos = false;
  bool failed = false;
  /* seek_locked runs under frame_lock, so an EOS restart cannot change the
   * pipeline state there (streaming thread / sink stream lock deadlock, the
   * same rule as tcs_player_set_paused). The caller applies it after release. */
  bool pending_play_restart = false;

  /* bus thread */
  std::atomic<bool> bus_running{false};
  std::thread bus_thread;

  /* D3D helpers (frame_lock held) */
  ID3D11Texture2D* single_tex = nullptr;
  D3D11_TEXTURE2D_DESC single_desc = {};
};

/* Volume with every mute source (user mute, load priming, D2 pause pump);
 * caller holds frame_lock. Internal helper: outside the extern "C" block. */
static void
apply_volume_locked (TcsPlayer* p)
{
  if (p->avolume)
    g_object_set (p->avolume, "volume",
        (p->muted || p->load_priming || p->pump_muted) ? 0.0 : p->volume_value / 100.0, nullptr);
}

static void
set_error (TcsPlayer* p, const char* fmt, ...)
{
  char buf[512];
  va_list args;
  va_start (args, fmt);
  vsnprintf (buf, sizeof (buf), fmt, args);
  va_end (args);
  std::lock_guard<std::mutex> g (p->frame_lock);
  p->last_error = buf;
  LOG ("%s", buf);
}

/* D2: one-line snapshot of the pipeline/sink state at a boundary.
 * Caller must NOT hold frame_lock. */
static void
log_pipe_state (TcsPlayer* p, const char* where)
{
  if (!paused_seek_diag || !p)
    return;
  GstState cur = GST_STATE_VOID_PENDING, pending = GST_STATE_VOID_PENDING;
  if (p->pipeline)
    gst_element_get_state (p->pipeline, &cur, &pending, 0);
  GstState vsink = GST_STATE_VOID_PENDING, vpending = GST_STATE_VOID_PENDING;
  if (p->appsink)
    gst_element_get_state (p->appsink, &vsink, &vpending, 0);
  gint64 pos = -1;
  if (p->pipeline)
    gst_element_query_position (p->pipeline, GST_FORMAT_TIME, &pos);
  guint64 frames = 0;
  guint32 queued = 0;
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    frames = p->frames_decoded;
    queued = (guint32) p->frames.size ();
  }
  LOG ("diag: %s paused=%d pipe=%d/%d vsink=%d/%d pos_ms=%.1f frames=%llu queued=%u",
      where, p->paused ? 1 : 0, (int) cur, (int) pending, (int) vsink, (int) vpending,
      (double) pos / 1e6, (unsigned long long) frames, queued);
}

static void
demote_foreign_gpu_decoders (void)
{
  /* decodebin is only reachable for audio and the unmatched-video fallback;
   * keep CUDA / D3D12 decoders out of its choices (their buffers do not share
   * our D3D11 device). */
  GstRegistry* reg = gst_registry_get ();
  const char* plugins[] = { "nvcodec", "d3d12", "va", "v4l2m2m", nullptr };
  for (int i = 0; plugins[i]; i++) {
    GList* feats = gst_registry_get_feature_list_by_plugin (reg, plugins[i]);
    for (GList* l = feats; l; l = l->next) {
      GstPluginFeature* f = GST_PLUGIN_FEATURE (l->data);
      if (strstr (gst_plugin_feature_get_name (f), "dec"))
        gst_plugin_feature_set_rank (f, GST_RANK_NONE);
    }
    g_list_free (feats);
  }
}

/* One-shot probe: can autoaudiosink actually open an output device?
 * On device-less sessions autoaudiosink fails/errors; the audio branch then
 * uses fakesink sync=true so video preroll/playback is unaffected. */
static bool
run_audio_probe (void)
{
  GstElement* src = gst_element_factory_make ("audiotestsrc", nullptr);
  GstElement* sink = gst_element_factory_make ("autoaudiosink", nullptr);
  GstElement* probe = gst_pipeline_new ("tcs-audio-probe");
  bool ok = false;
  if (!probe) {
    if (src) gst_object_unref (src);
    if (sink) gst_object_unref (sink);
    return false;
  }
  if (!src || !sink) {
    if (src) gst_object_unref (src);
    if (sink) gst_object_unref (sink);
    gst_object_unref (probe);
    return false;
  }
  g_object_set (src, "num-buffers", 1, "volume", 0.0, nullptr);
  gst_bin_add_many (GST_BIN (probe), src, sink, nullptr);
  if (gst_element_link (src, sink)) {
    gst_element_set_state (probe, GST_STATE_PLAYING);
    GstBus* bus = gst_element_get_bus (probe);
    GstMessage* msg = gst_bus_timed_pop_filtered (bus, 2 * GST_SECOND,
        (GstMessageType) (GST_MESSAGE_EOS | GST_MESSAGE_ERROR));
    ok = msg && GST_MESSAGE_TYPE (msg) == GST_MESSAGE_EOS;
    if (msg)
      gst_message_unref (msg);
    gst_object_unref (bus);
    gst_element_set_state (probe, GST_STATE_NULL);
  }
  gst_object_unref (probe);
  return ok;
}

static bool
audio_sink_usable (void)
{
  static std::once_flag probe_once;
  static bool probe_ok = false;
  std::call_once (probe_once, [] { probe_ok = run_audio_probe (); });
  return probe_ok;
}

static IDXGIAdapter*
find_adapter_by_luid (LUID luid)
{
  IDXGIFactory1* factory = nullptr;
  if (FAILED (CreateDXGIFactory1 (__uuidof(IDXGIFactory1), (void**) &factory)) || !factory)
    return nullptr;
  IDXGIAdapter1* adapter = nullptr;
  for (UINT i = 0; factory->EnumAdapters1 (i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
    DXGI_ADAPTER_DESC1 desc = {};
    adapter->GetDesc1 (&desc);
    if (memcmp (&desc.AdapterLuid, &luid, sizeof (LUID)) == 0) {
      factory->Release ();
      return adapter;
    }
    adapter->Release ();
  }
  factory->Release ();
  return nullptr;
}

/* Stage 6b: the shim NEVER adopts the compositor device. When the owner
 * passes one we read its adapter LUID (IDXGIDevice::GetAdapter) and create a
 * separate device + immediate context on that adapter; the compositor device
 * is not AddRef'd and its context is not used. external == NULL keeps the
 * default-adapter behavior for standalone use (shim smoke test, CPU output). */
static gboolean
create_or_adopt_device (TcsPlayer* p, ID3D11Device* external)
{
  IDXGIAdapter* adapter = nullptr;
  const char* origin = "internal-default";
  if (external) {
    IDXGIDevice* dxgi = nullptr;
    if (FAILED (external->QueryInterface (__uuidof(IDXGIDevice), (void**) &dxgi)) || !dxgi) {
      set_error (p, "external device lacks IDXGIDevice (cannot read the adapter LUID)");
      return FALSE;
    }
    IDXGIAdapter* external_adapter = nullptr;
    HRESULT hr = dxgi->GetAdapter (&external_adapter);
    dxgi->Release ();
    if (FAILED (hr) || !external_adapter) {
      set_error (p, "external device GetAdapter failed hr=0x%08lx", hr);
      return FALSE;
    }
    DXGI_ADAPTER_DESC desc = {};
    external_adapter->GetDesc (&desc);
    external_adapter->Release ();
    adapter = find_adapter_by_luid (desc.AdapterLuid);
    if (!adapter) {
      set_error (p, "no adapter with the compositor LUID was found");
      return FALSE;
    }
    origin = "external-luid";
  }

  UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
  D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
  UINT nLevels = adapter ? 1 : 2;
  HRESULT hr = D3D11CreateDevice (adapter, adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE,
      nullptr, flags, levels, nLevels, D3D11_SDK_VERSION, &p->device, nullptr, &p->context);
  if (adapter)
    adapter->Release ();
  if (FAILED (hr)) {
    set_error (p, "D3D11CreateDevice failed hr=0x%08lx", hr);
    return FALSE;
  }
  p->owns_device = true;
  LOG ("device created origin=%s (separate from the compositor device)", origin);

  if (!p->context) {
    set_error (p, "no immediate context");
    return FALSE;
  }
  /* GStreamer streams from other threads; enable multithread protection on
   * OUR device explicitly (short calls, no Flush/GetData loops elsewhere). */
  ID3D11Multithread* mt = nullptr;
  if (SUCCEEDED (p->context->QueryInterface (__uuidof(ID3D11Multithread), (void**) &mt)) && mt) {
    mt->SetMultithreadProtected (TRUE);
    mt->Release ();
  }
  /* the device must expose the video interfaces for d3d11 dec/convert */
  ID3D11VideoDevice* vd = nullptr;
  if (FAILED (p->device->QueryInterface (__uuidof(ID3D11VideoDevice), (void**) &vd)) || !vd) {
    set_error (p, "device lacks ID3D11VideoDevice (needs D3D11_CREATE_DEVICE_VIDEO_SUPPORT)");
    return FALSE;
  }
  vd->Release ();

  if (SUCCEEDED (p->device->QueryInterface (__uuidof(ID3D11Device5), (void**) &p->device5)) && p->device5)
    LOG ("ring: ID3D11Device5 available");
  else
    LOG ("ring: ID3D11Device5 unavailable; falling back to the legacy lease path");
  if (SUCCEEDED (p->context->QueryInterface (__uuidof(ID3D11DeviceContext4), (void**) &p->context4)) && p->context4) {
    /* ok */
  } else {
    LOG ("ring: ID3D11DeviceContext4 unavailable; falling back to the legacy lease path");
  }

  p->gst_dev = gst_d3d11_device_new_wrapped (p->device);
  if (!p->gst_dev) {
    set_error (p, "gst_d3d11_device_new_wrapped failed");
    return FALSE;
  }
  return TRUE;
}

/* Adapter LUID of a D3D11 device (0:0 when it cannot be read). Used by the
 * D16 diagnostics to name both devices in one log line. */
static bool
device_luid (ID3D11Device* dev, LUID* out)
{
  out->HighPart = 0;
  out->LowPart = 0;
  IDXGIDevice* dxgi = nullptr;
  if (!dev || FAILED (dev->QueryInterface (__uuidof(IDXGIDevice), (void**) &dxgi)) || !dxgi)
    return false;
  IDXGIAdapter* adapter = nullptr;
  HRESULT hr = dxgi->GetAdapter (&adapter);
  dxgi->Release ();
  if (FAILED (hr) || !adapter)
    return false;
  DXGI_ADAPTER_DESC desc = {};
  adapter->GetDesc (&desc);
  adapter->Release ();
  *out = desc.AdapterLuid;
  return true;
}

static void
give_device_context (TcsPlayer* p, GstElement* el);

/* decodebin / late-plugged d3d11 elements post NEED_CONTEXT on the bus;
 * answer with OUR device so the whole chain stays on one ID3D11Device. */
static GstBusSyncReply
sync_bus_handler (GstBus* /*bus*/, GstMessage* msg, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  if (GST_MESSAGE_TYPE (msg) == GST_MESSAGE_NEED_CONTEXT) {
    const gchar* type = nullptr;
    gst_message_parse_context_type (msg, &type);
    if (g_strcmp0 (type, GST_D3D11_DEVICE_HANDLE_CONTEXT_TYPE) == 0 &&
        GST_IS_ELEMENT (GST_MESSAGE_SRC (msg))) {
      give_device_context (p, GST_ELEMENT (GST_MESSAGE_SRC (msg)));
      return GST_BUS_DROP;
    }
  }
  return GST_BUS_PASS;
}

static void
give_device_context (TcsPlayer* p, GstElement* el)
{
  if (!p->gst_dev)
    return;
  GstContext* ctx = gst_context_new (GST_D3D11_DEVICE_HANDLE_CONTEXT_TYPE, TRUE);
  GstStructure* s = gst_context_writable_structure (ctx);
  gst_structure_set (s, "device", GST_TYPE_D3D11_DEVICE, p->gst_dev,
      "adapter", G_TYPE_UINT, 0u, nullptr);
  gst_element_set_context (el, ctx);
  gst_context_unref (ctx);
}

/* ---------------- shared texture ring (stage 6b, frame_lock) ---------------- */

/* Release the ring resources. The NT handles were created by us and must be
 * closed here (CreateSharedHandle ownership). Safe on partially built rings;
 * the compositor's opened references keep their resources alive after this. */
static void
destroy_ring (TcsPlayer* p)
{
  for (uint32_t i = 0; i < TcsPlayer::kRingSlots; i++) {
    if (p->ring_handle[i]) { CloseHandle (p->ring_handle[i]); p->ring_handle[i] = nullptr; }
    if (p->ring_texture[i]) { p->ring_texture[i]->Release (); p->ring_texture[i] = nullptr; }
  }
  if (p->ring_fence_handle) { CloseHandle (p->ring_fence_handle); p->ring_fence_handle = nullptr; }
  if (p->ring_fence) { p->ring_fence->Release (); p->ring_fence = nullptr; }
  p->ring_ready = false;
  p->ring_width = p->ring_height = 0;
  p->ring_epoch = 0;
}

/* Create the 3-slot BGRA ring + shared fence. D8: rebuilt whenever the
 * requested dimensions differ from the current ring; the epoch increments on
 * every build so the compositor can detect the change and reopen the handles.
 * Caller holds frame_lock. */
static bool
ensure_ring_locked (TcsPlayer* p, int width, int height)
{
  if (p->ring_ready && p->ring_width == width && p->ring_height == height)
    return true;
  if (!p->device5 || !p->context4)
    return false;
  if (width <= 0 || height <= 0)
    return false;

  int old_w = p->ring_width, old_h = p->ring_height;
  bool rebuilt = p->ring_ready;
  uint32_t next_epoch = p->ring_epoch + 1;  /* destroy_ring resets it to 0 */
  if (next_epoch == 0)
    next_epoch = 1;                         /* 0 is reserved for "no ring" */
  if (rebuilt)
  {
    /* Frames queued for the old ring can no longer be handed out through it.
     * Their samples keep the decoder pool alive, so unref them and count the
     * drop as a replacement. */
    for (auto it = p->frames.begin (); it != p->frames.end (); )
    {
      if (it->slot >= 0)
      {
        gst_sample_unref (it->sample);
        it = p->frames.erase (it);
        p->delivery_replaced++;
      }
      else
      {
        ++it;
      }
    }
    /* The compositor's opened handles keep the old allocations alive; the shim
     * drops its own references. */
    destroy_ring (p);
  }

  D3D11_TEXTURE2D_DESC d = {};
  d.Width = (UINT) width;
  d.Height = (UINT) height;
  d.MipLevels = 1;
  d.ArraySize = 1;
  d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
  d.SampleDesc.Count = 1;
  d.Usage = D3D11_USAGE_DEFAULT;
  d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
  d.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

  for (uint32_t i = 0; i < TcsPlayer::kRingSlots; i++) {
    if (FAILED (p->device->CreateTexture2D (&d, nullptr, &p->ring_texture[i])) || !p->ring_texture[i]) {
      LOG ("ring: CreateTexture2D failed slot=%u", i);
      destroy_ring (p);
      return false;
    }
    IDXGIResource1* res = nullptr;
    if (FAILED (p->ring_texture[i]->QueryInterface (__uuidof(IDXGIResource1), (void**) &res)) || !res) {
      LOG ("ring: IDXGIResource1 unavailable slot=%u", i);
      destroy_ring (p);
      return false;
    }
    HRESULT hr = res->CreateSharedHandle (nullptr, GENERIC_ALL, nullptr, &p->ring_handle[i]);
    res->Release ();
    if (FAILED (hr) || !p->ring_handle[i]) {
      LOG ("ring: texture CreateSharedHandle failed slot=%u hr=0x%08lx", i, hr);
      destroy_ring (p);
      return false;
    }
  }
  if (FAILED (p->device5->CreateFence (0, D3D11_FENCE_FLAG_SHARED, __uuidof(ID3D11Fence),
      (void**) &p->ring_fence)) || !p->ring_fence) {
    LOG ("ring: CreateFence failed");
    destroy_ring (p);
    return false;
  }
  if (FAILED (p->ring_fence->CreateSharedHandle (nullptr, GENERIC_ALL, nullptr,
      &p->ring_fence_handle)) || !p->ring_fence_handle) {
    LOG ("ring: fence CreateSharedHandle failed");
    destroy_ring (p);
    return false;
  }
  p->ring_width = width;
  p->ring_height = height;
  p->ring_ready = true;
  p->ring_epoch = next_epoch;
  if (rebuilt)
    LOG ("ring: recreated %dx%d epoch=%u (was %dx%d)",
        width, height, p->ring_epoch, old_w, old_h);
  else
    LOG ("ring: created %dx%d BGRA slots=%u epoch=%u",
        width, height, TcsPlayer::kRingSlots, p->ring_epoch);
  return true;
}

/* A ring slot is busy while the current lease holds it or an undelivered FIFO
 * item refers to it. Pure allocation rule lives in tcs_delivery_policy.h. */
static int32_t
pick_free_ring_slot_locked (TcsPlayer* p)
{
  uint8_t used[TcsPlayer::kRingSlots] = {};
  tcs_ring_mark_occupied (used, TcsPlayer::kRingSlots, p->leased_slot,
      p->lease_info.ring_epoch, p->ring_epoch);
  for (const TcsPlayer::FrameSlot& f : p->frames)
    tcs_ring_mark_occupied (used, TcsPlayer::kRingSlots, f.slot,
        f.ring_epoch, p->ring_epoch);
  return tcs_ring_pick_slot (used, TcsPlayer::kRingSlots);
}

/* Latest-first catch-up: evict the oldest undelivered GPU item so its slot can
 * take the new arrival (counted as replaced by the caller). */
static bool
evict_oldest_ring_item_locked (TcsPlayer* p)
{
  int32_t item_slots[TcsPlayer::kFrameQueueCapacity + 8];
  const uint32_t cap = (uint32_t) (sizeof (item_slots) / sizeof (item_slots[0]));
  uint32_t n = 0;
  for (const TcsPlayer::FrameSlot& f : p->frames) {
    if (n >= cap)
      break;
    item_slots[n++] = f.slot;
  }
  int32_t idx = tcs_ring_evict_index (item_slots, n);
  if (idx < 0)
    return false;
  auto it = p->frames.begin () + idx;
  if (it->sample)
    gst_sample_unref (it->sample);
  p->frames.erase (it);
  return true;
}

/* Copy the decoded sample texture into the ring slot (array slices use a box
 * copy). Caller then signals the fence with the frame seq. */
static void
copy_ring_locked (TcsPlayer* p, int32_t slot, ID3D11Texture2D* src, guint sub,
                  const D3D11_TEXTURE2D_DESC& desc)
{
  if (desc.ArraySize == 1 && sub == 0) {
    p->context->CopyResource (p->ring_texture[slot], src);
    return;
  }
  D3D11_BOX box = {};
  box.right = MIN (desc.Width, (UINT) p->ring_width);
  box.bottom = MIN (desc.Height, (UINT) p->ring_height);
  box.back = 1;
  p->context->CopySubresourceRegion (p->ring_texture[slot], 0, 0, 0, 0, src, sub, &box);
}

/* ---------------- frame delivery ---------------- */

/* Append one event to the delivery ring and return its slot. Caller holds
 * frame_lock: on_new_sample, tcs_player_get_time_pos and the drain all
 * serialize there, so every ring access is ordered. The returned pointer
 * stays valid for one callback (ring_size >> frame rate); callers only touch
 * it before returning to their own caller. */
static TcsDeliveryEvent*
delivery_append_locked (TcsPlayer* p, uint64_t qpc, uint64_t seq, int64_t pts_ns,
                        int64_t running_ns, uint32_t flags)
{
  TcsDeliveryEvent* e = &p->delivery_ring[p->delivery_write % TcsPlayer::kDeliveryCapacity];
  e->qpc = qpc;
  e->seq = seq;
  e->pts_ns = pts_ns;
  e->running_ns = running_ns;
  e->callback_us = 0;
  e->flags = flags;
  p->delivery_write++;
  if (p->delivery_write - p->delivery_read > TcsPlayer::kDeliveryCapacity) {
    p->delivery_read = p->delivery_write - TcsPlayer::kDeliveryCapacity;
    p->delivery_ring_dropped++;
  }
  return e;
}

/* QoS events (upstream) and decoder output buffers are counted so the
 * delivery trace can distinguish source-side drops from scheduling. */
static GstPadProbeReturn
on_qos_probe (GstPad*, GstPadProbeInfo* info, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  if (GST_EVENT_TYPE (GST_PAD_PROBE_INFO_EVENT (info)) == GST_EVENT_QOS)
    p->delivery_qos.fetch_add (1, std::memory_order_relaxed);
  return GST_PAD_PROBE_OK;
}

static GstPadProbeReturn
on_audio_sink_probe (GstPad*, GstPadProbeInfo*, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  p->audio_sink_buffers.fetch_add (1, std::memory_order_relaxed);
  return GST_PAD_PROBE_OK;
}

/* Method 2 (clock rebase) helper: the real basesink inside the audio chain
 * (autoaudiosink is a bin; its child is wasapi2sink/directsoundsink/...). */
static GstElement*
find_audio_sink_element (TcsPlayer* p)
{
  if (!p->asink)
    return nullptr;
  if (g_object_class_find_property (G_OBJECT_GET_CLASS (p->asink), "ts-offset"))
    return p->asink;
  if (GST_IS_BIN (p->asink)) {
    GstElement* found = nullptr;
    GstIterator* it = gst_bin_iterate_recurse (GST_BIN (p->asink));
    GValue v = G_VALUE_INIT;
    while (!found && gst_iterator_next (it, &v) == GST_ITERATOR_OK) {
      GstElement* child = GST_ELEMENT (g_value_get_object (&v));
      if (g_object_class_find_property (G_OBJECT_GET_CLASS (child), "ts-offset"))
        found = child;
      g_value_reset (&v);
    }
    gst_iterator_free (it);
    return found;
  }
  return nullptr;
}

/* Method 2: rewrite the post-seek SEGMENT at each sink so the snap distance
 * collapses to zero, exactly like qtdemux's accurate seek does internally
 * (segment.start = target). The sink then drops every pre-target buffer as
 * out-of-segment *before* preroll, so the pipeline waits only for the target
 * frame to be decoded and then anchors base_time on it: target = "now",
 * later frames pace normally, and audio (same rewritten segment) starts at
 * the target. The original event is dropped and replaced via gst_pad_send_event
 * (which does not re-enter probes), so downstream sees exactly one segment. */
static GstPadProbeReturn
on_sink_segment_rewrite (GstPad* pad, GstPadProbeInfo* info, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  GstEvent* ev = GST_PAD_PROBE_INFO_EVENT (info);
  if (!ev || GST_EVENT_TYPE (ev) != GST_EVENT_SEGMENT)
    return GST_PAD_PROBE_OK;
  /* Method-linked: rebase_seek_seqnum is non-zero only for keyunit seeks
   * (any container). Forced accurate on TS leaves it 0 and no rewrite runs. */
  guint64 target = 0;
  guint32 seek_seq = 0;
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    target = p->gate_target_ns;
    seek_seq = p->rebase_seek_seqnum;
  }
  if (target == 0 || seek_seq == 0 || gst_event_get_seqnum (ev) != seek_seq)
    return GST_PAD_PROBE_OK;
  const GstSegment* seg = nullptr;
  gst_event_parse_segment (ev, &seg);
  if (!seg || seg->format != GST_FORMAT_TIME || seg->rate <= 0.0 ||
      seg->start >= target)
    return GST_PAD_PROBE_OK;
  GstSegment ns = *seg;
  ns.start = target;
  if (ns.time < target)
    ns.time = target;
  ns.position = target;
  ns.offset = 0;
  ns.base = 0;
  if (p->audio_rewrite_sink &&
      (gpointer) GST_PAD_PARENT (pad) == (gpointer) p->audio_rewrite_sink) {
    p->au_probe_buffers.store (0, std::memory_order_relaxed);
    p->au_probe_first_pts.store (-1, std::memory_order_relaxed);
    p->au_probe_last_pts.store (-1, std::memory_order_relaxed);
  } else if (p->appsink &&
      (gpointer) GST_PAD_PARENT (pad) == (gpointer) p->appsink) {
    p->vp_probe_buffers.store (0, std::memory_order_relaxed);
    p->vp_probe_first_pts.store (-1, std::memory_order_relaxed);
    p->vp_probe_last_pts.store (-1, std::memory_order_relaxed);
  }
  GstEvent* newev = gst_event_new_segment (&ns);
  gst_event_set_seqnum (newev, seek_seq);
  gboolean sent = gst_pad_send_event (pad, newev);
  LOG ("seek: %s segment rewritten sink=%s old_start=%" GST_TIME_FORMAT
      " target=%" GST_TIME_FORMAT " rate=%.3f sent=%d",
      p->mpegts ? "ts" : "keyunit",
      GST_PAD_PARENT (pad) ? GST_ELEMENT_NAME (GST_PAD_PARENT (pad)) : "?",
      GST_TIME_ARGS (seg->start), GST_TIME_ARGS (target), seg->rate, sent);
  return GST_PAD_PROBE_DROP;
}

/* Diagnostic: remember the first/last pts reaching the audio sink after a
 * rewritten segment (a seek that never opens the gate can be told apart from
 * "no audio buffer ever arrived"). */
static GstPadProbeReturn
on_audio_buffer_diag (GstPad*, GstPadProbeInfo* info, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  GstBuffer* b = GST_PAD_PROBE_INFO_BUFFER (info);
  if (!b)
    return GST_PAD_PROBE_OK;
  guint64 pts = GST_BUFFER_PTS (b);
  if (pts == GST_CLOCK_TIME_NONE)
    return GST_PAD_PROBE_OK;
  uint64_t prev = p->au_probe_buffers.fetch_add (1, std::memory_order_relaxed);
  if (prev == 0)
    p->au_probe_first_pts.store ((int64_t) pts, std::memory_order_relaxed);
  p->au_probe_last_pts.store ((int64_t) pts, std::memory_order_relaxed);
  return GST_PAD_PROBE_OK;
}

/* Diagnostic: count the buffers actually reaching the video appsink pad after
 * a TS seek (before the sink's segment clipping). */
static GstPadProbeReturn
on_video_buffer_diag (GstPad*, GstPadProbeInfo* info, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  GstBuffer* b = GST_PAD_PROBE_INFO_BUFFER (info);
  if (!b)
    return GST_PAD_PROBE_OK;
  guint64 pts = GST_BUFFER_PTS (b);
  if (pts == GST_CLOCK_TIME_NONE)
    return GST_PAD_PROBE_OK;
  uint64_t prev = p->vp_probe_buffers.fetch_add (1, std::memory_order_relaxed);
  if (prev == 0)
    p->vp_probe_first_pts.store ((int64_t) pts, std::memory_order_relaxed);
  p->vp_probe_last_pts.store ((int64_t) pts, std::memory_order_relaxed);
  return GST_PAD_PROBE_OK;
}

/* Install the rewrite probes once per pipeline: on the video appsink and on
 * the real audio sink. Called on the first TS seek (the autoaudiosink child
 * only exists after the chain has been built). */
static void
ensure_sink_segment_rewrites (TcsPlayer* p)
{
  if (!p->video_rewrite_installed && p->appsink) {
    GstPad* pad = gst_element_get_static_pad (p->appsink, "sink");
    if (pad) {
      gst_pad_add_probe (pad, GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM,
          on_sink_segment_rewrite, p, nullptr);
      gst_pad_add_probe (pad, GST_PAD_PROBE_TYPE_BUFFER, on_video_buffer_diag, p, nullptr);
      p->video_rewrite_installed = true;
      gst_object_unref (pad);
    }
  }
  if (p->asink) {
    GstElement* sink = find_audio_sink_element (p);
    if (sink && sink != p->audio_rewrite_sink) {
      GstPad* pad = gst_element_get_static_pad (sink, "sink");
      if (pad) {
        gst_pad_add_probe (pad, GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM,
            on_sink_segment_rewrite, p, nullptr);
        gst_pad_add_probe (pad, GST_PAD_PROBE_TYPE_BUFFER, on_audio_buffer_diag, p, nullptr);
        p->audio_rewrite_sink = sink;
        gst_object_unref (pad);
      }
    }
  }
}

/* First downstream SEGMENT after a TS seek: diagnostics only (the actual
 * rewrite happens at the sinks, see on_sink_segment_rewrite). */
static GstPadProbeReturn
on_demux_segment_probe (GstPad*, GstPadProbeInfo* info, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  GstEvent* ev = GST_PAD_PROBE_INFO_EVENT (info);
  if (!ev || GST_EVENT_TYPE (ev) != GST_EVENT_SEGMENT)
    return GST_PAD_PROBE_OK;
  uint64_t target = 0;
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    if (!p->rebase_armed)
      return GST_PAD_PROBE_OK;
    p->rebase_armed = false;
    target = p->gate_target_ns;
    p->rebase_count++;
    p->av_log_seq = p->frames_decoded;
  }
  const GstSegment* seg = nullptr;
  gst_event_parse_segment (ev, &seg);
  if (!seg || seg->format != GST_FORMAT_TIME || seg->rate <= 0.0) {
    LOG ("seek: %s rebase diagnostics skipped (format/rate)", p->mpegts ? "ts" : "keyunit");
    return GST_PAD_PROBE_OK;
  }
  guint64 r_start = gst_segment_to_running_time ((GstSegment*) seg,
      GST_FORMAT_TIME, seg->start);
  guint64 r_target = gst_segment_to_running_time ((GstSegment*) seg,
      GST_FORMAT_TIME, (guint64) target);
  LOG ("seek: %s snap seg_start=%" GST_TIME_FORMAT " target=%" GST_TIME_FORMAT
      " r_start=%" GST_TIME_FORMAT " r_target=%" GST_TIME_FORMAT
      " snap_ms=%.1f rate=%.3f",
      p->mpegts ? "ts" : "keyunit",
      GST_TIME_ARGS (seg->start), GST_TIME_ARGS (target),
      GST_TIME_ARGS (r_start), GST_TIME_ARGS (r_target),
      (double) (target - seg->start) / 1e6, seg->rate);
  return GST_PAD_PROBE_OK;
}

/* ---- D25: flush-boundary marker for samples ----
 * A flushing seek bumps the generation, but a sample that the streaming
 * thread had already pulled out of the appsink before the flush reached the
 * sink is still in flight; on_new_sample then stamped it with the post-seek
 * generation and acquire() handed it out as the target frame (V5 1-in-10:
 * target=14.618 lease=5.250; F-4: the pre-seek picture with a post-seek
 * sequence). The pad probe below tags every buffer entering the appsink with
 * the number of flushing seeks that had passed the pad at that instant. The
 * probe runs on the streaming thread in the same order as the seek event, so
 * the tag is an exact pre/post-seek marker; on_new_sample drops a sample whose
 * tag is older than the shim's seek serial. */
typedef struct {
  GstMeta meta;
  uint64_t boundary;   /* flushing seek events that had passed the pad */
} TcsFlushMeta;

static GType
tcs_flush_meta_api_get_type (void)
{
  static GType type = 0;
  if (g_once_init_enter (&type)) {
    static const gchar* tags[] = { nullptr };
    GType t = gst_meta_api_type_register ("TcsFlushMetaAPI", tags);
    g_once_init_leave (&type, t);
  }
  return type;
}

static gboolean
tcs_flush_meta_init (GstMeta* meta, gpointer params, GstBuffer* buffer)
{
  (void) params;
  (void) buffer;
  ((TcsFlushMeta*) meta)->boundary = 0;
  return TRUE;
}

static void
tcs_flush_meta_free (GstMeta* meta, GstBuffer* buffer)
{
  (void) meta;
  (void) buffer;
}

static const GstMetaInfo*
tcs_flush_meta_info (void);

static gboolean
tcs_flush_meta_transform (GstBuffer* dest, GstMeta* meta, GstBuffer* src,
    GQuark type, gpointer data)
{
  (void) src;
  if (GST_META_TRANSFORM_IS_COPY (type)) {
    GstMetaTransformCopy* copy = (GstMetaTransformCopy*) data;
    if (!copy->region) {
      TcsFlushMeta* d = (TcsFlushMeta*) gst_buffer_add_meta (dest,
          tcs_flush_meta_info (), nullptr);
      if (!d)
        return FALSE;
      d->boundary = ((TcsFlushMeta*) meta)->boundary;
    }
    return TRUE;
  }
  return FALSE;
}

static const GstMetaInfo*
tcs_flush_meta_info (void)
{
  static const GstMetaInfo* info = nullptr;
  static bool tried = false;
  if (!tried) {
    tried = true;
    GType api = tcs_flush_meta_api_get_type ();
    if (api != 0)
      info = gst_meta_register (api, "TcsFlushMeta",
          sizeof (TcsFlushMeta), tcs_flush_meta_init, tcs_flush_meta_free,
          tcs_flush_meta_transform);
    if (!info)
      LOG ("D25: flush-meta registration failed; pre-seek filtering disabled");
  }
  return info;
}

static GstPadProbeReturn
on_appsink_flush_probe (GstPad*, GstPadProbeInfo* info, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  GstPadProbeType type = GST_PAD_PROBE_INFO_TYPE (info);
  if (type & GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM) {
    /* The boundary event is the post-seek SEGMENT: the demuxer consumes the
     * SEEK event and sends no FLUSH_START through the sink pad (both verified
     * with the pad-event diagnostic: seek arrived upstream, then segment
     * downstream; counting SEEK/FLUSH_START dropped every post-seek sample).
     * The new segment always precedes the first post-seek buffer on the
     * streaming thread. */
    GstEvent* ev = GST_PAD_PROBE_INFO_EVENT (info);
    if (ev && GST_EVENT_TYPE (ev) == GST_EVENT_SEGMENT)
      p->flush_boundary.fetch_add (1, std::memory_order_acq_rel);
  } else if (type & GST_PAD_PROBE_TYPE_BUFFER) {
    GstBuffer* buf = GST_PAD_PROBE_INFO_BUFFER (info);
    const GstMetaInfo* mi = tcs_flush_meta_info ();
    if (buf && mi) {
      TcsFlushMeta* m = (TcsFlushMeta*) gst_buffer_add_meta (buf, mi, nullptr);
      if (m)
        m->boundary = p->flush_boundary.load (std::memory_order_acquire);
    }
  }
  return GST_PAD_PROBE_OK;
}

/* D25: true when the sample was already past the appsink pad when the latest
 * seek's segment was expected (its tag predates the seek boundary). Samples
 * without the marker (probe not installed, e.g. no buffer path) are kept so a
 * probe problem cannot stop playback; the one-time log flags them. */
static bool
sample_is_pre_seek (TcsPlayer* p, GstBuffer* buf)
{
  uint64_t expect = p->seek_boundary_expect.load (std::memory_order_acquire);
  if (expect == 0)
    return false;
  /* Safety bound: if the seek's segment never reaches the appsink pad (a
   * collapsed back-to-back flush, a failed flush), filtering must not drop
   * frames forever. After 5s (pump budget 4s + margin) the expectation is
   * abandoned and samples pass. */
  if (GetTickCount64 () - p->seek_expect_ms.load (std::memory_order_relaxed) > 5000) {
    p->seek_boundary_expect.store (0, std::memory_order_release);
    return false;
  }
  const TcsFlushMeta* m = nullptr;
  GType api = tcs_flush_meta_api_get_type ();
  if (buf && api != 0)
    m = (const TcsFlushMeta*) gst_buffer_get_meta (buf, api);
  if (!m) {
    if (!p->flush_marker_missing.exchange (true))
      LOG ("D25: sample without flush marker after a seek; keeping it "
          "(probe not on the buffer path?)");
    return false;
  }
  return m->boundary < expect;
}

static GstFlowReturn
on_new_sample (GstAppSink* sink, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  LARGE_INTEGER arrival;
  QueryPerformanceCounter (&arrival);
  GstSample* sample = gst_app_sink_try_pull_sample (sink, 0);
  if (!sample)
    return GST_FLOW_OK;

  GstBuffer* buf = gst_sample_get_buffer (sample);
  if (sample_is_pre_seek (p, buf)) {
    /* D25: never publish a pre-seek sample under the post-seek generation. */
    uint64_t drops = p->stale_drops.fetch_add (1, std::memory_order_relaxed) + 1;
    if (drops == 1 || (drops % 120) == 0)
      LOG ("D25: dropped pre-seek sample pts_ms=%.1f (drops=%llu)",
          buf && GST_BUFFER_PTS (buf) != GST_CLOCK_TIME_NONE
              ? (double) GST_BUFFER_PTS (buf) / 1e6 : -1.0,
          (unsigned long long) drops);
    gst_sample_unref (sample);
    return GST_FLOW_OK;
  }
  GstMemory* mem = buf ? gst_buffer_peek_memory (buf, 0) : nullptr;
  bool gpu = mem && gst_is_d3d11_memory (mem);
  /* Map with GST_MAP_D3D11 for the whole arrival: this flushes a pending
   * sysmem->texture upload (d3d11upload writes to a staging texture and only
   * issues the CopySubresourceRegion on the next D3D11 map; reading the
   * texture without it yields black frames). Harmless for decoder memory. */
  GstMapInfo d3d_map = {};
  bool d3d_mapped = false;
  if (gpu)
    d3d_mapped = gst_memory_map (mem, &d3d_map,
        (GstMapFlags) (GST_MAP_READ | GST_MAP_D3D11));
  /* Stage 6b: extract the decoded texture now so the ring copy can run under
   * frame_lock (the sample ref keeps the pool texture alive until release). */
  ID3D11Texture2D* src_tex = nullptr;
  guint src_sub = 0;
  D3D11_TEXTURE2D_DESC src_desc = {};
  if (gpu) {
    GstD3D11Memory* dmem = (GstD3D11Memory*) mem;
    ID3D11Resource* res = gst_d3d11_memory_get_resource_handle (dmem);
    src_sub = gst_d3d11_memory_get_subresource_index (dmem);
    if (res && SUCCEEDED (res->QueryInterface (__uuidof(ID3D11Texture2D), (void**) &src_tex)) && src_tex)
      src_tex->GetDesc (&src_desc);
    else
      src_tex = nullptr;  /* legacy sample path handles the flatten later */
  }
  /* D16 defense: never pass a foreign-device resource to p->context (or to the
   * compositor). A hybrid GPU decoder can create its own device on another
   * adapter; fail the load instead of crashing in the driver. */
  if (src_tex && p->device) {
    ID3D11Device* src_dev = nullptr;
    src_tex->GetDevice (&src_dev);
    if (!src_dev || src_dev != p->device) {
      LUID src_luid = {}, shim_luid = {};
      device_luid (src_dev, &src_luid);
      device_luid (p->device, &shim_luid);
      if (src_dev)
        src_dev->Release ();
      src_tex->Release ();
      if (d3d_mapped)
        gst_memory_unmap (mem, &d3d_map);
      {
        std::lock_guard<std::mutex> g (p->frame_lock);
        p->failed = true;
      }
      set_error (p, "on_new_sample: decoder memory is on another adapter "
          "(src_luid=%08lx:%08lx shim_luid=%08lx:%08lx)",
          (unsigned long) src_luid.HighPart, (unsigned long) src_luid.LowPart,
          (unsigned long) shim_luid.HighPart, (unsigned long) shim_luid.LowPart);
      gst_sample_unref (sample);
      return GST_FLOW_OK;
    }
    src_dev->Release ();
  }
  const GstSegment* seg = gst_sample_get_segment (sample);
  bool has_buffer_pts = buf && GST_BUFFER_PTS (buf) != GST_CLOCK_TIME_NONE;
  guint64 raw_pts = has_buffer_pts
      ? GST_BUFFER_PTS (buf)
      : (seg ? (guint64) seg->position : 0);
  int64_t running_ns = -1;
  if (seg && raw_pts != GST_CLOCK_TIME_NONE) {
    guint64 rt = gst_segment_to_running_time (seg, GST_FORMAT_TIME, raw_pts);
    if (rt != GST_CLOCK_TIME_NONE)
      running_ns = (int64_t) rt;
  }
  /* D10: report stream time, not the raw buffer PTS. qtdemux (B-frames,
   * negative first DTS) shifts every post-seek timestamp by the first-DTS
   * compensation, so the raw PTS reads 2 frames ahead of the actual sample
   * (segment start=15.0333 / time=15.0 maps back to 15.0). The gate below and
   * the running time keep the raw PTS: the gate compares decoded samples
   * against the seek target and running time is the scheduling clock. A buffer
   * without PTS already carries the segment position (stream time). */
  guint64 pts = raw_pts;
  if (has_buffer_pts) {
    int stream_time_fallback = 0;
    pts = tcs_stream_time_or_pts (seg, raw_pts, &stream_time_fallback);
    if (stream_time_fallback && !stream_time_fallback_logged.exchange (true))
      LOG ("D10: no segment stream-time mapping; reporting raw PTS pts_ms=%.2f",
          (double) raw_pts / 1e6);
  }

  /* demux pads (e.g. mpegts) may expose stream caps without width/height;
   * the negotiated sample caps always carry the real geometry. */
  int cw = 0, ch = 0, cdn = 0, cdd = 0;
  GstCaps* sample_caps = gst_sample_get_caps (sample);
  if (sample_caps && gst_caps_get_size (sample_caps) > 0) {
    GstStructure* ssc = gst_caps_get_structure (sample_caps, 0);
    gst_structure_get_int (ssc, "width", &cw);
    gst_structure_get_int (ssc, "height", &ch);
    gst_structure_get_fraction (ssc, "framerate", &cdn, &cdd);
  }

  tcs_frame_notify_fn cb = nullptr;
  void* cb_user = nullptr;
  uint64_t cb_gen = 0, cb_seq = 0;
  bool replaced = false;
  TcsDeliveryEvent* delivery_event = nullptr;
  bool gated = false;
  bool log_av = false;
  uint64_t log_av_seq = 0, log_av_gen = 0, log_av_target_ns = 0;
  {
    /* D2 rework diagnostics: the streaming thread holds the appsink stream lock
     * here. If a state change holds frame_lock (the documented deadlock), this
     * contention line is the last one before the hang. */
    /* v0.5.0: HAP の解析と Snappy の展開は CPU だけの処理なので、frame_lock の外で行う。
     * 4K では 1 コマ数 ms かかり、ロックの中でやると位置の問い合わせ（UI）と取得（GPU worker）を
     * 待たせていた（内蔵 GPU の検証機で、開始直後に frame_lock の競合が増えてコマが落ちた）。
     * hap_buffer はこのストリーミングスレッドだけが触る。GPU への転送は shim の context を使うので、
     * これまでどおりロックの中で行う。 */
    bool hap_ready = false;
    TcsHapFrameInfo hap_info = {};
    if (p->hap_stream && buf) {
      GstMapInfo map = {};
      if (!gst_buffer_map (buf, &map, GST_MAP_READ)) {
        p->hap_decode_failures++;
        LOG ("hap: buffer map failed (failures=%llu)", (unsigned long long) p->hap_decode_failures);
      } else {
        if (!tcs_hap_parse (map.data, map.size, &hap_info)) {
          p->hap_decode_failures++;
          LOG ("hap: parse failed size=%zu (failures=%llu)", (size_t) map.size,
              (unsigned long long) p->hap_decode_failures);
        } else {
          if (p->hap_buffer.size () < hap_info.decompressed_length)
            p->hap_buffer.resize (hap_info.decompressed_length);
          if (!tcs_hap_decompress_frame (map.data, map.size, &hap_info, p->hap_buffer.data (),
                  p->hap_buffer.size ())) {
            p->hap_decode_failures++;
            LOG ("hap: decompress failed bytes=%u (failures=%llu)", hap_info.decompressed_length,
                (unsigned long long) p->hap_decode_failures);
          } else {
            hap_ready = true;
          }
        }
        gst_buffer_unmap (buf, &map);
      }
    }
    std::unique_lock<std::mutex> g (p->frame_lock, std::try_to_lock);
    if (!g.owns_lock()) {
      LOG ("on_new_sample: frame_lock busy; waiting (state change in progress?)");
      g.lock();
    }
    if (test_hold_frame_lock_ms > 0 && test_hold_budget.fetch_sub (1) > 0)
      Sleep ((DWORD) test_hold_frame_lock_ms);
    if (p->seg_diag_left > 0) {
      p->seg_diag_left--;
      LOG ("seek: diag vsample pts_ms=%.1f seg_start_ms=%.1f seg_base_ms=%.1f "
          "seg_time_ms=%.1f seg_rate=%.3f running_ms=%.1f gated=%d",
          (double) raw_pts / 1e6, seg ? (double) seg->start / 1e6 : -1.0,
          seg ? (double) seg->base / 1e6 : -1.0,
          seg ? (double) seg->time / 1e6 : -1.0, seg ? seg->rate : 0.0,
          (double) running_ns / 1e6, p->gate_active ? 1 : 0);
    }
    /* Method 5 gate: after a TS keyframe-snap seek, every decoded frame whose
     * pts is before the target is dropped here (decode keeps running). The
     * compositor keeps drawing Held while acquire() reports none; the gate
     * opening must NOT bump the generation again (the seek did that once). */
    bool gate_opened = false;
    if (p->gate_active) {
      if (p->gate_first_qpc == 0) {
        p->gate_first_qpc = (uint64_t) arrival.QuadPart;
        p->gate_first_pts_ns = raw_pts;
      }
      if (raw_pts < p->gate_target_ns) {
        p->gate_dropped++;
        gated = true;
      } else {
        p->gate_active = false;
        gate_opened = true;
        double ms_per_tick = p->qpc_freq > 0 ? 1000.0 / (double) p->qpc_freq : 0.0;
        LOG ("seek: ts gate opened target_ns=%llu snap_pts_ns=%llu first_pts_ns=%llu "
            "dropped=%llu first_ms=%.1f open_ms=%.1f",
            (unsigned long long) p->gate_target_ns,
            (unsigned long long) p->gate_first_pts_ns, (unsigned long long) raw_pts,
            (unsigned long long) p->gate_dropped,
            (double) (p->gate_first_qpc - p->gate_armed_qpc) * ms_per_tick,
            (double) ((uint64_t) arrival.QuadPart - p->gate_armed_qpc) * ms_per_tick);
      }
    }
    /* v0.5.0: 展開済み（ロックの外）の HAP を GPU で BGRA のテクスチャにし、以降は復号済みの
     * フレームと同じ扱いにする（リングへのコピーは共通の経路）。context は shim のものを使うので、
     * frame_lock の中で行う。 */
    if (hap_ready && !gated) {
      if (!p->hap_gpu)
        p->hap_gpu = tcs_hap_gpu_create (p->device, p->context);
      ID3D11Texture2D* hap_tex = p->hap_gpu
          ? tcs_hap_gpu_decode (p->hap_gpu, p->hap_buffer.data (),
                hap_info.decompressed_length, hap_info.texture_format, cw, ch)
          : nullptr;
      if (!hap_tex) {
        p->hap_decode_failures++;
        LOG ("hap: gpu decode failed %dx%d format=0x%02X reason=%s (failures=%llu)",
            cw, ch, hap_info.texture_format,
            p->hap_gpu ? tcs_hap_gpu_last_error (p->hap_gpu) : "no gpu helper",
            (unsigned long long) p->hap_decode_failures);
        /* 扱えない変種（Hap 7 / Hap HDR など）は、黙って絵を出さずに読み込みを失敗させる。
         * set_error は frame_lock を取るのでここからは呼べない。直接書く。 */
        if (p->hap_gpu && tcs_hap_gpu_unsupported_format (p->hap_gpu) >= 0) {
          p->failed = true;
          p->last_error = tcs_hap_gpu_last_error (p->hap_gpu);
        }
      } else {
        hap_tex->GetDesc (&src_desc);
        /* 以降は復号済みフレームと同じ扱いになり、最後に src_tex を Release する。
         * このテクスチャは HAP の展開器が持ち続けるので、参照を 1 つ足して釣り合わせる。 */
        hap_tex->AddRef ();
        src_tex = hap_tex;
        src_sub = 0;
        gpu = true;
      }
    }
    if (!gated) {
      if (cw > 0) p->width = cw;
      if (ch > 0) p->height = ch;
      if (cdn > 0 && cdd > 0) p->fps = (double) cdn / (double) cdd;
      p->latest_gen = p->generation;
      p->latest_pts_ns = pts;
      /* D25: the fence value must be strictly increasing for the lifetime of
       * the shared fence. frames_decoded restarts at 0 on every load while
       * the ring/fence persist, so a post-load seq reused an already-completed
       * fence value and the compositor's IsRingFenceComplete() passed before
       * the new copy had run (old pixels with a new Info). frames_decoded
       * stays the per-load counter the load path waits on. */
      p->latest_seq = ++p->seq_serial;
      p->frames_decoded++;
      p->latest_gpu = gpu;
      /* Method 2 diagnostics: sample the audio sink position next to the
       * delivered video pts (gate-open frame plus a decimated window). */
      if (gate_opened ||
          (p->av_log_left > 0 &&
           (p->av_log_stride == 0 || (p->latest_seq % p->av_log_stride) == 0))) {
        log_av = true;
        log_av_seq = p->latest_seq;
        log_av_gen = p->latest_gen;
        log_av_target_ns = p->gate_target_ns;
      }
      if (p->av_log_left > 0)
        p->av_log_left--;

      /* Stage 6b: GPU samples go through the shared ring. If every slot is
       * busy, evict the oldest undelivered frame first (latest-first catch-up,
       * counted as replaced / flags bit0). D8: ensure_ring_locked rebuilds the
       * ring when the dimensions changed, so a frame never falls back to the
       * legacy sample path because of a resolution change. */
      int32_t ring_slot = -1;
      uint32_t ring_slot_epoch = 0;
      if (gpu && src_tex && cw > 0 && ch > 0 && ensure_ring_locked (p, cw, ch) &&
          (int) src_desc.Width == p->ring_width && (int) src_desc.Height == p->ring_height &&
          src_desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM) {
        int32_t slot = pick_free_ring_slot_locked (p);
        if (slot < 0 && evict_oldest_ring_item_locked (p)) {
          replaced = true;
          p->delivery_replaced++;
          slot = pick_free_ring_slot_locked (p);
        }
        if (slot >= 0) {
          copy_ring_locked (p, slot, src_tex, src_sub, src_desc);
          p->context4->Signal (p->ring_fence, p->latest_seq);
          p->context->Flush ();  /* submit copy+signal (no wait here) */
          ring_slot = slot;
          ring_slot_epoch = p->ring_epoch;
        }
      }
      if (ring_slot < 0 && p->frames.size() >= TcsPlayer::kFrameQueueCapacity) {
        gst_sample_unref (p->frames.front().sample);
        p->frames.pop_front();
        replaced = true;
        p->delivery_replaced++;
      }
      p->frames.push_back (TcsPlayer::FrameSlot{sample, p->generation, p->latest_seq, pts,
          (uint64_t) arrival.QuadPart, gpu, ring_slot, ring_slot_epoch});
      p->pending_update = true;
      p->delivery_arrivals++;
      p->delivery_last_qpc = (uint64_t) arrival.QuadPart;
      cb = p->notify;
      cb_user = p->notify_user;
      cb_gen = p->latest_gen;
      cb_seq = p->latest_seq;
      /* Record the arrival before invoking the owner's callback: the callback
       * may block, but the event must not wait for it. callback_us is filled
       * in place afterwards (single writer; the ring cannot wrap within one
       * callback at ring_size >> frame rate). */
      delivery_event = delivery_append_locked (p, (uint64_t) arrival.QuadPart,
          p->latest_seq, (int64_t) pts, running_ns,
          (replaced ? 1u : 0u) | (cb ? 2u : 0u) | (gpu ? 4u : 0u));
    }
  }

  if (d3d_mapped)
    gst_memory_unmap (mem, &d3d_map);

  if (frame_log && !gated)
    LOG ("frame: qpc=%llu pts_ms=%.2f dts_ms=%.2f dur_ms=%.2f pts_valid=%d "
        "seq=%llu",
        (unsigned long long) arrival.QuadPart, (double) pts / 1e6,
        buf && GST_BUFFER_DTS (buf) != GST_CLOCK_TIME_NONE
            ? (double) GST_BUFFER_DTS (buf) / 1e6 : -1.0,
        buf && GST_BUFFER_DURATION (buf) != GST_CLOCK_TIME_NONE
            ? (double) GST_BUFFER_DURATION (buf) / 1e6 : -1.0,
        buf && GST_BUFFER_PTS (buf) != GST_CLOCK_TIME_NONE ? 1 : 0,
        (unsigned long long) cb_seq);

  if (gated) {
    if (src_tex)
      src_tex->Release ();
    gst_sample_unref (sample);
    return GST_FLOW_OK;
  }

  if (log_av) {
    gint64 apos = -1;
    gboolean aok = FALSE;
    if (p->asink) {
      GstElement* asink = find_audio_sink_element (p);
      if (asink) {
        aok = gst_element_query_position (asink, GST_FORMAT_TIME, &apos);
        if (!aok)
          LOG ("av: diag audio sink=%s query failed", GST_ELEMENT_NAME (asink));
      } else {
        LOG ("av: diag no ts-offset sink found under %s", GST_ELEMENT_NAME (p->asink));
      }
    }
    LOG ("av: qpc=%llu gen=%llu seq=%llu video_pts=%.3f target=%.3f "
        "audio_pos=%.3f diff_ms=%.1f aok=%d",
        (unsigned long long) arrival.QuadPart,
        (unsigned long long) log_av_gen, (unsigned long long) log_av_seq,
        (double) pts / GST_SECOND, (double) log_av_target_ns / GST_SECOND,
        aok ? (double) apos / GST_SECOND : -1.0,
        aok ? (double) ((gint64) pts - apos) / 1e6 : 0.0, aok);
  }

  if (cb) {
    LARGE_INTEGER c0, c1, qfreq;
    QueryPerformanceCounter (&c0);
    cb (cb_user, cb_gen, cb_seq);
    QueryPerformanceCounter (&c1);
    QueryPerformanceFrequency (&qfreq);
    if (delivery_event != nullptr)
      delivery_event->callback_us = (uint32_t) (((c1.QuadPart - c0.QuadPart) * 1000000) /
          (qfreq.QuadPart ? qfreq.QuadPart : 1));
  }
  if (src_tex)
    src_tex->Release ();
  return GST_FLOW_OK;
}

static const char*
caps_first_media (GstCaps* caps)
{
  if (!caps || gst_caps_is_any (caps) || gst_caps_is_empty (caps))
    return nullptr;
  return gst_structure_get_name (gst_caps_get_structure (caps, 0));
}

static void
record_video_caps (TcsPlayer* p, GstCaps* caps)
{
  if (!caps || gst_caps_get_size (caps) == 0)
    return;
  GstStructure* s = gst_caps_get_structure (caps, 0);
  int w = 0, h = 0, dn = 0, dd = 0;
  gst_structure_get_int (s, "width", &w);
  gst_structure_get_int (s, "height", &h);
  gst_structure_get_fraction (s, "framerate", &dn, &dd);
  std::lock_guard<std::mutex> g (p->frame_lock);
  if (w > 0) p->width = w;
  if (h > 0) p->height = h;
  if (dd > 0 && dn > 0) p->fps = (double) dn / (double) dd;
}

/* ---------------- explicit codec branching ----------------

 * Profiles are tried in a fixed order (plus a cached last-good profile).
 * For every attempt the FULL chain (parse ! decoder ! converter ! caps !
 * appsink) is built statically before the PAUSED state change; the demux
 * pad-added callback only links its pad to the chain head. Building the
 * sink side lazily inside pad-added misses/delays the preroll (observed:
 * pipeline stuck without error). */

static gboolean sync_pacing = TRUE;

static void create_appsink_tail (TcsPlayer* p, gboolean d3d, const char* caps_override = nullptr);

typedef TcsVideoProfile VideoProfile;
static const VideoProfile* const g_profiles = kTcsVideoProfiles;
static const int kProfileCount = TCS_VIDEO_PROFILE_COUNT;
#define PROFILE_INDEX_FALLBACK (-1)
static_assert (PROFILE_INDEX_FALLBACK == TCS_DECODE_PROFILE_FALLBACK,
    "fallback index must match tcs_decode_policy.h");

/* CPU decode profiles (decode to sysmem, uploaded with d3d11upload) versus
 * GPU profiles (d3d11*dec -> d3d11colorconvert). Derived from the converter
 * so reordering the table stays safe (see tcs_video_profiles.h). */
static bool
profile_is_software (int idx)
{
  return tcs_video_profile_is_software (idx) != 0;
}

/* v0.5.0: HAP の経路を通すか（既定は無効。全条件に通ってから既定で有効にする）。 */
static bool
hap_enabled ()
{
  static const bool enabled = [] {
    /* v0.5.0: 検証機で全条件に通ったので既定で有効。TCS_HAP=off のときだけ無効にする
     * （無効のときは従来どおり、CPU の展開へは流さずに読み込みを断る）。 */
    char buf[16] = {};
    DWORD n = GetEnvironmentVariableA ("TCS_HAP", buf, sizeof (buf));
    bool off = n > 0 && n < sizeof (buf) && _stricmp (buf, "off") == 0;
    LOG ("hap: %s (TCS_HAP=off で無効)", off ? "disabled" : "enabled");
    return !off;
  } ();
  return enabled;
}

static gboolean
caps_is_hap (GstCaps* caps)
{
  const char* mt = caps_first_media (caps);
  return mt && g_str_has_prefix (mt, "video/x-hap");
}

static gboolean
caps_is_raw_video (GstCaps* caps)
{
  const char* mt = caps_first_media (caps);
  return mt && g_str_has_prefix (mt, "video/x-raw");
}

static gboolean
profile_matches_caps (const VideoProfile* prof, GstCaps* caps)
{
  const char* mt = caps_first_media (caps);
  if (!mt)
    return FALSE;
  if (g_str_has_prefix (mt, prof->media))
    return TRUE;
  if (prof->media2 && g_str_has_prefix (mt, prof->media2))
    return TRUE;
  return FALSE;
}

/* D16: the GPU profiles must decode on the shim's own device (the ring
 * device). On a hybrid GPU the d3d11 decoder can create its own device on
 * another adapter for a codec the shim adapter cannot decode (AV1 on an AMD
 * iGPU) and then hand foreign-device memory to p->context. Query the decoder
 * profiles of the shim adapter and skip such GPU profiles before building
 * their chain. CPU profiles return true (no adapter restriction).
 * The GUID values are the SDK's D3D11_DECODER_PROFILE_* constants (the
 * d3d11.lib does not export the symbols, so they are repeated here). */
static const GUID kGuidH264VldNofgt = {
  0x1b81be68, 0xa0c7, 0x11d3, { 0xb9, 0x84, 0x00, 0xc0, 0x4f, 0x2e, 0x73, 0xc5 } };
static const GUID kGuidHevcVldMain = {
  0x5b11d51b, 0x2f4c, 0x4452, { 0xbc, 0xc3, 0x09, 0xf2, 0xa1, 0x16, 0x0c, 0xc0 } };
static const GUID kGuidVp9VldProfile0 = {
  0x463707f8, 0xa1d0, 0x4585, { 0x87, 0x6d, 0x83, 0xaa, 0x6d, 0x60, 0xb8, 0x9e } };
static const GUID kGuidAv1VldProfile0 = {
  0xb8be4ccb, 0xcf53, 0x46ba, { 0x8d, 0x59, 0xd6, 0xb8, 0xa6, 0xda, 0x5d, 0x2a } };

static const GUID*
profile_decoder_guid (int idx)
{
  if (idx < 0 || idx >= kProfileCount)
    return nullptr;
  const char* dec = g_profiles[idx].dec;
  if (dec == nullptr)
    return nullptr;
  if (g_strcmp0 (dec, "d3d11h264dec") == 0)
    return &kGuidH264VldNofgt;
  if (g_strcmp0 (dec, "d3d11h265dec") == 0)
    return &kGuidHevcVldMain;
  if (g_strcmp0 (dec, "d3d11vp9dec") == 0)
    return &kGuidVp9VldProfile0;
  if (g_strcmp0 (dec, "d3d11av1dec") == 0)
    return &kGuidAv1VldProfile0;
  return nullptr;
}

static bool
adapter_supports_profile (TcsPlayer* p, int idx)
{
  const GUID* want = profile_decoder_guid (idx);
  if (!want)
    return true;
  if (env_flag ("TCS_FORCE_DECODER_ADAPTER_MISMATCH"))
    return false;  /* test hook: exercise the CPU fallback */
  ID3D11VideoDevice* vd = nullptr;
  if (!p->device ||
      FAILED (p->device->QueryInterface (__uuidof(ID3D11VideoDevice), (void**) &vd)) || !vd)
    return true;   /* cannot tell: keep the previous behavior */
  const UINT n = vd->GetVideoDecoderProfileCount ();
  bool found = false;
  for (UINT i = 0; i < n && !found; i++) {
    GUID g;
    if (SUCCEEDED (vd->GetVideoDecoderProfile (i, &g)) &&
        memcmp (&g, want, sizeof (GUID)) == 0)
      found = true;
  }
  vd->Release ();
  return found;
}

/* D16-b: d3d11 decoder classes are registered per adapter at plugin load
 * (d3d11h264dec = the first enumerated adapter, d3d11h264deviceNdec = the
 * N-th). The class adapter-luid decides which device the element creates, and
 * a context on another adapter is rejected, so a decoder whose class LUID
 * differs from the ring device never uses our device. Pick the class that
 * matches; otherwise report the mismatch so the GPU profile is skipped. */
static bool
decoder_matches_shim_adapter (TcsPlayer* p, int idx, std::string* name_out)
{
  if (idx < 0 || idx >= kProfileCount || profile_is_software (idx))
    return true;   /* CPU profile: no adapter restriction */
  const char* base = g_profiles[idx].dec;
  if (!base)
    return true;
  *name_out = base;
  LUID luid = {};
  if (!device_luid (p->device, &luid))
    return true;   /* cannot tell: keep the previous behavior */
  const gint64 want = gst_d3d11_luid_to_int64 (&luid);
  const bool force = env_flag ("TCS_FORCE_DECODER_LUID_MISMATCH");
  char candidate[64];
  for (int i = 0; i < 8; i++) {
    if (i == 0) {
      if (strlen (base) >= sizeof (candidate))
        return true;
      snprintf (candidate, sizeof (candidate), "%s", base);
    } else {
      size_t prefix = strlen (base) >= 3 ? strlen (base) - 3 : 0;
      if (prefix + 16 >= sizeof (candidate))
        return true;
      snprintf (candidate, sizeof (candidate), "%.*sdevice%ddec", (int) prefix, base, i);
    }
    GstElement* el = gst_element_factory_make (candidate, nullptr);
    if (!el)
      continue;
    gint64 actual = 0;
    g_object_get (el, "adapter-luid", &actual, nullptr);
    gst_object_unref (el);
    if (!force && actual == want) {
      *name_out = candidate;
      LOG ("decoder class: %s luid=%016llx matches shim device", candidate,
          (unsigned long long) actual);
      return true;
    }
    LOG ("decoder class: %s luid=%016llx shim=%016llx%s", candidate,
        (unsigned long long) actual, (unsigned long long) want,
        force ? " (forced mismatch)" : "");
  }
  return false;
}

/* V11-e measurement switch: feed avdec_* the multithreading properties from the
 * environment. Unset (the default) leaves the properties untouched, so the
 * shipped behavior is unchanged. Properties that the element does not have are
 * skipped silently. */
static void
apply_decode_thread_env (GstElement* dec)
{
  if (!dec)
    return;
  char type[16];
  DWORD type_n = GetEnvironmentVariableA ("TCS_DECODE_THREAD_TYPE", type, sizeof (type));
  char threads[16];
  DWORD threads_n = GetEnvironmentVariableA ("TCS_DECODE_MAX_THREADS", threads, sizeof (threads));
  if (type_n == 0 && threads_n == 0)
    return;
  GObjectClass* klass = G_OBJECT_GET_CLASS (dec);
  if (type_n > 0 && type_n < sizeof (type))
    {
      guint value = G_MAXUINT;
      if (g_ascii_strcasecmp (type, "frame") == 0) value = 1;
      else if (g_ascii_strcasecmp (type, "slice") == 0) value = 2;
      else if (g_ascii_strcasecmp (type, "auto") == 0) value = 0;
      if (value == G_MAXUINT)
        LOG ("decode-thread-type: unknown value '%s' -> ignored", type);
      else if (g_object_class_find_property (klass, "thread-type"))
        {
          g_object_set (dec, "thread-type", value, nullptr);
          LOG ("decode-thread-type: %s (0x%x) on %s", type, value,
              GST_ELEMENT_NAME (dec));
        }
    }
  if (threads_n > 0 && threads_n < sizeof (threads))
    {
      gint value = atoi (threads);
      if (value < 0)
        LOG ("decode-max-threads: invalid value '%s' -> ignored", threads);
      else if (g_object_class_find_property (klass, "max-threads"))
        {
          g_object_set (dec, "max-threads", value, nullptr);
          LOG ("decode-max-threads: %d on %s", value, GST_ELEMENT_NAME (dec));
        }
    }
}

/* V11-h: log the element order actually linked, one line per built chain. The
 * hardware chain must contain exactly one d3d11colorconvert; the CPU chain is
 * videoconvert -> d3d11upload -> d3d11colorconvert (the CPU-side videoconvert
 * passes through whenever d3d11upload accepts the decoder format). */
static void
log_video_chain (const char* profile, GstElement* const* chain, int n)
{
  std::string names;
  for (int i = 0; i < n; i++) {
    if (!chain[i])
      continue;
    if (!names.empty ())
      names += ",";
    names += GST_ELEMENT_NAME (chain[i]);
  }
  LOG ("video-chain: %s elements=%s", profile, names.c_str ());
}

/* D13: a small queue directly after the demux video pad decouples the demux
 * thread from appsink preroll (a video-first MP4 otherwise blocks the demux
 * while the audio sink still has to preroll). Time/byte limits are disabled so
 * the bound is exactly the buffer count; a flushing seek empties the queue. */
static void
configure_video_queue (GstElement* q)
{
  g_object_set (q, "max-size-buffers", (guint) 4,
      "max-size-time", (guint64) 0, "max-size-bytes", (guint) 0, nullptr);
}

/* ---------------- 0.4.5-C: long-GOP probe ---------------- */

/* One probe per built chain. The context carries the epoch it was installed
 * for so a late callback from a torn-down pipeline removes itself without
 * touching the player's state (D34-style stale-callback guard). */
struct GopProbeContext {
  TcsPlayer* player;
  uint64_t epoch;
};

static void
gop_probe_context_free (gpointer data)
{
  delete (GopProbeContext*) data;
}

/* Streaming thread. Reads PTS / DELTA_UNIT only (never maps, copies or drops
 * a buffer) and always reports GST_PAD_PROBE_OK, except for the stale-epoch
 * REMOVE. Holds gop_lock, a leaf (see TcsPlayer); the single LOG on latch
 * runs after the lock is released. */
static GstPadProbeReturn
on_gop_probe (GstPad* /*pad*/, GstPadProbeInfo* info, gpointer user)
{
  GopProbeContext* ctx = (GopProbeContext*) user;
  TcsPlayer* p = ctx->player;
  if (p->gop_epoch.load (std::memory_order_acquire) != ctx->epoch)
    return GST_PAD_PROBE_REMOVE;
  GstBuffer* buf = GST_PAD_PROBE_INFO_BUFFER (info);
  if (!buf)
    return GST_PAD_PROBE_OK;
  GstClockTime pts = GST_BUFFER_PTS (buf);
  if (!GST_CLOCK_TIME_IS_VALID (pts))
    return GST_PAD_PROBE_OK;
  int is_keyframe = GST_BUFFER_FLAG_IS_SET (buf, GST_BUFFER_FLAG_DELTA_UNIT) ? 0 : 1;
  const uint64_t now = qpc_now ();
  int latched = 0;
  int64_t median_ms = 0, threshold_ms = 0;
  uint64_t keyframes = 0;
  uint32_t intervals = 0;
  {
    std::lock_guard<std::mutex> g (p->gop_lock);
    if (p->gop_epoch.load (std::memory_order_acquire) != ctx->epoch)
      return GST_PAD_PROBE_REMOVE;
    if (p->gop_reanchor.exchange (false, std::memory_order_acq_rel))
      tcs_gop_tracker_reanchor (&p->gop_tracker);
    latched = tcs_gop_tracker_observe (&p->gop_tracker, is_keyframe,
        (int64_t) pts, now);
    if (latched) {
      median_ms = p->gop_tracker.median_interval_ns / 1000000;
      threshold_ms = p->gop_tracker.threshold_ns / 1000000;
      keyframes = p->gop_tracker.keyframes;
      intervals = p->gop_tracker.interval_count < TCS_GOP_INTERVAL_CAPACITY
          ? p->gop_tracker.interval_count : TCS_GOP_INTERVAL_CAPACITY;
    }
  }
  if (latched)
    LOG ("long-gop: warning latched keyframes=%llu intervals=%u "
        "median_interval_ms=%lld threshold_ms=%lld",
        (unsigned long long) keyframes, (unsigned) intervals,
        (long long) median_ms, (long long) threshold_ms);
  return GST_PAD_PROBE_OK;
}

/* Attach the detector to the pad that carries encoded frames: the parser src
 * pad when the profile has one, otherwise the demux video pad (ProRes). The
 * decodebin fallback never calls this and reports active=0. */
static void
install_gop_probe (TcsPlayer* p, GstPad* pad)
{
  {
    std::lock_guard<std::mutex> g (p->gop_lock);
    if (p->gop_probe_installed)
      return;
    p->gop_probe_installed = true;
    p->gop_active = true;
  }
  GopProbeContext* ctx = new GopProbeContext {
      p, p->gop_epoch.load (std::memory_order_acquire) };
  gst_pad_add_probe (pad, GST_PAD_PROBE_TYPE_BUFFER, on_gop_probe, ctx,
      gop_probe_context_free);
}

/* Build the static video tail for profile index idx (-1 = decodebin
 * fallback). Elements are added, given the device context and linked;
 * on_demux_pad_added only links the demux pad to p->vhead. */
static gboolean
build_video_chain_static (TcsPlayer* p, int idx)
{
  p->vchain_built = FALSE;
  p->capsMismatch = FALSE;
  p->rejected = FALSE;
  p->vProfile = idx;

  if (p->hap_stream) {
    /* v0.5.0: HAP は復号せずに受ける。queue -> appsink(video/x-hap) だけの連鎖にして、
     * 圧縮テクスチャのまま on_new_sample へ渡す（展開と BGRA 化は shim の中で行う）。
     * デコーダを挟まないので d3d11upload も色変換も要らない。 */
    p->vqueue = gst_element_factory_make ("queue", nullptr);
    p->vhead = p->vqueue;
    create_appsink_tail (p, FALSE, "video/x-hap");
    if (!p->vqueue || !p->vcaps || !p->appsink) {
      set_error (p, "hap chain factory failed");
      return FALSE;
    }
    configure_video_queue (p->vqueue);
    gst_bin_add_many (GST_BIN (p->pipeline), p->vqueue, p->vcaps, p->appsink, nullptr);
    give_device_context (p, p->pipeline);
    if (!gst_element_link_many (p->vqueue, p->vcaps, p->appsink, nullptr)) {
      set_error (p, "hap chain link failed");
      return FALSE;
    }
    {
      GstElement* chain[] = { p->vqueue, p->vcaps, p->appsink };
      log_video_chain ("hap-gpu", chain, 3);
    }
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->decoder_name = "hap(gpu)";
    return TRUE;
  }

  if (idx == PROFILE_INDEX_FALLBACK) {
    /* Last resort for unmatched video/*: the container is decodebin, so its
     * src pad is already decoded raw video. videoconvert -> d3d11upload ->
     * d3d11colorconvert keeps the shared-ring delivery contract (CPU decode,
     * GPU lease) with the format conversion on the GPU (V11-h). */
    p->vqueue = gst_element_factory_make ("queue", nullptr);
    p->vhead = gst_element_factory_make ("videoconvert", nullptr);
    p->vupload = gst_element_factory_make ("d3d11upload", nullptr);
    p->vgpuconvert = gst_element_factory_make ("d3d11colorconvert", nullptr);
    create_appsink_tail (p, TRUE);
    if (!p->vqueue || !p->vhead || !p->vupload || !p->vgpuconvert || !p->vcaps || !p->appsink) {
      set_error (p, "fallback chain factory failed");
      return FALSE;
    }
    configure_video_queue (p->vqueue);
    gst_bin_add_many (GST_BIN (p->pipeline), p->vqueue, p->vhead, p->vupload,
        p->vgpuconvert, p->vcaps, p->appsink, nullptr);
    give_device_context (p, p->vhead);
    give_device_context (p, p->vupload);
    give_device_context (p, p->vgpuconvert);
    give_device_context (p, p->pipeline);
    if (!gst_element_link_many (p->vqueue, p->vhead, p->vupload, p->vgpuconvert,
            p->vcaps, p->appsink, nullptr)) {
      set_error (p, "fallback chain link failed");
      return FALSE;
    }
    {
      GstElement* chain[] = { p->vqueue, p->vhead, p->vupload, p->vgpuconvert,
          p->vcaps, p->appsink };
      log_video_chain ("decodebin-fallback", chain, 6);
    }
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->decoder_name = "decodebin(sysmem)";
    return TRUE;
  }

  const VideoProfile* prof = &g_profiles[idx];
  gboolean d3d = strstr (prof->conv, "d3d11") != nullptr;

  GstElement* head = nullptr;
  p->vqueue = gst_element_factory_make ("queue", nullptr);
  p->vparse = prof->parse ? gst_element_factory_make (prof->parse, nullptr) : nullptr;
  const char* dec_name = (!profile_is_software (idx) && !p->decoder_element_name.empty ())
      ? p->decoder_element_name.c_str () : prof->dec;
  p->vdec = dec_name ? gst_element_factory_make (dec_name, nullptr) : nullptr;
  apply_decode_thread_env (p->vdec);
  p->vconvert = gst_element_factory_make (prof->conv, nullptr);
  /* CPU profiles decode to sysmem; d3d11upload moves it to the shim device and
   * the post-upload d3d11colorconvert finishes the format conversion on the
   * GPU (V11-h). The CPU-side videoconvert stays as the pass-through safety
   * net for formats d3d11upload cannot take. */
  p->vupload = d3d ? nullptr : gst_element_factory_make ("d3d11upload", nullptr);
  p->vgpuconvert = d3d ? nullptr : gst_element_factory_make ("d3d11colorconvert", nullptr);
  create_appsink_tail (p, TRUE);
  if (!p->vqueue || !p->vconvert || !p->vcaps || !p->appsink ||
      (prof->parse && !p->vparse) || (prof->dec && !p->vdec) ||
      (!d3d && (!p->vupload || !p->vgpuconvert))) {
    set_error (p, "chain factory failed for profile %s", prof->name);
    return FALSE;
  }
  configure_video_queue (p->vqueue);
  head = p->vqueue;
  p->vhead = head;

  gst_bin_add (GST_BIN (p->pipeline), p->vqueue);
  if (p->vparse) gst_bin_add (GST_BIN (p->pipeline), p->vparse);
  if (p->vdec) gst_bin_add (GST_BIN (p->pipeline), p->vdec);
  gst_bin_add (GST_BIN (p->pipeline), p->vconvert);
  if (p->vupload) gst_bin_add (GST_BIN (p->pipeline), p->vupload);
  if (p->vgpuconvert) gst_bin_add (GST_BIN (p->pipeline), p->vgpuconvert);
  gst_bin_add (GST_BIN (p->pipeline), p->vcaps);
  gst_bin_add (GST_BIN (p->pipeline), p->appsink);

  if (p->vparse) give_device_context (p, p->vparse);
  if (p->vdec) give_device_context (p, p->vdec);
  give_device_context (p, p->vconvert);
  if (p->vupload) give_device_context (p, p->vupload);
  if (p->vgpuconvert) give_device_context (p, p->vgpuconvert);
  give_device_context (p, p->pipeline);

  GstElement* chain[] = { p->vqueue, p->vparse, p->vdec, p->vconvert, p->vupload,
      p->vgpuconvert, p->vcaps, p->appsink, nullptr };
  int nChain = (int) (sizeof (chain) / sizeof (chain[0])) - 1;
  GstElement* prev = nullptr;
  for (int i = 0; i < nChain; i++) {
    if (!chain[i]) continue;
    if (prev && !gst_element_link (prev, chain[i])) {
      set_error (p, "static chain link failed at %s", GST_OBJECT_NAME (prev));
      return FALSE;
    }
    prev = chain[i];
  }
  log_video_chain (prof->name, chain, nChain);
  if (p->vparse) {
    GstPad* srcpad = gst_element_get_static_pad (p->vparse, "src");
    if (srcpad) {
      install_gop_probe (p, srcpad);
      gst_object_unref (srcpad);
    }
  }
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->decoder_name = prof->dec ? prof->dec : "raw";
  }
  return TRUE;
}

/* V11-g measurement switch: appsink max-buffers. Unset keeps the shipped 4.
 * 0 is passed through as GStreamer's "unlimited" and is only useful for
 * experiments; it is not a supported product configuration. */
static uint32_t
appsink_max_buffers ()
{
  char buf[16];
  DWORD n = GetEnvironmentVariableA ("TCS_APPSINK_MAX_BUFFERS", buf, sizeof (buf));
  if (n == 0 || n >= sizeof (buf))
    return 4u;
  int value = atoi (buf);
  if (value < 0)
    {
      LOG ("appsink max-buffers: invalid value '%s' -> 4", buf);
      return 4u;
    }
  return (uint32_t) value;
}

/* Create appsink + capsfilter for the tail. d3d selects D3D11Memory BGRA.
 * caps_override (v0.5.0) lets the HAP chain ask for the compressed pad instead. */
static void
create_appsink_tail (TcsPlayer* p, gboolean d3d, const char* caps_override)
{
  p->vcaps = gst_element_factory_make ("capsfilter", nullptr);
  p->appsink = gst_element_factory_make ("appsink", nullptr);
  GstCaps* caps = gst_caps_from_string (
      caps_override ? caps_override
          : d3d ? "video/x-raw(memory:D3D11Memory),format=BGRA"
                : "video/x-raw,format=BGRA");
  g_object_set (p->vcaps, "caps", caps, nullptr);
  gst_caps_unref (caps);
  GstAppSinkCallbacks cbs = {};
  cbs.new_sample = on_new_sample;
  gst_app_sink_set_callbacks (GST_APP_SINK (p->appsink), &cbs, p, nullptr);
  uint32_t max_buffers = appsink_max_buffers ();
  g_object_set (p->appsink, "emit-signals", TRUE, "sync", sync_pacing, "drop", FALSE,
      "max-buffers", (guint) max_buffers, nullptr);
  if (max_buffers != 4u)
    LOG ("appsink max-buffers: %u (TCS_APPSINK_MAX_BUFFERS)", (unsigned) max_buffers);
  GstPad* sinkpad = gst_element_get_static_pad (p->appsink, "sink");
  if (sinkpad) {
    gst_pad_add_probe (sinkpad,
        (GstPadProbeType) (GST_PAD_PROBE_TYPE_EVENT_UPSTREAM | GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM),
        on_qos_probe, p, nullptr);
    /* D25: tag every buffer with the seek boundary and count the downstream
     * SEGMENT events (both on the streaming thread, in order). */
    gst_pad_add_probe (sinkpad,
        (GstPadProbeType) (GST_PAD_PROBE_TYPE_BUFFER | GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM),
        on_appsink_flush_probe, p, nullptr);
    gst_object_unref (sinkpad);
  }
  p->use_d3d11_caps = d3d;
}

static void
on_video_pad (TcsPlayer* p, GstPad* pad, GstCaps* caps)
{
  if (p->vchain_built) /* extra video track: ignore, first one wins */
    return;
  if (caps_is_hap (caps)) {
    /* v0.5.0: 圧縮テクスチャのまま受ける経路（hap-gpu）。**CPU デコーダには決して渡さない。**
     * 既定では無効で、TCS_HAP=on のときだけ通す。 */
    if (!hap_enabled ()) {
      p->rejected = true;
      set_error (p, "video/x-hap requires the reserved compressed-texture branch (refusing decodebin/avdec)");
      return;
    }
    if (!p->hap_stream || !p->vhead) {
      /* この試行はほかの形式向けに組んである。HAP だと分かったので、組み直して次の試行で通す。 */
      p->hap_stream = true;
      p->capsMismatch = true;
      LOG ("hap: video/x-hap detected; rebuilding the chain for the compressed-texture path");
      return;
    }
    /* hap の連鎖はすでに組んである（vhead = queue）。プロファイルの照合は通さずに直結する。 */
    GstPad* hap_sink = gst_element_get_static_pad (p->vhead, "sink");
    if (hap_sink) {
      gst_pad_link_full (pad, hap_sink, GST_PAD_LINK_CHECK_NOTHING);
      gst_object_unref (hap_sink);
    }
    p->vchain_built = true;
    return;
  }
  if (p->hap_stream) {
    /* HAP の素材なのに raw の pad が来た = decodebin が CPU で展開している。使わない。 */
    p->rejected = true;
    set_error (p, "video/x-hap must not be decoded on the CPU (decodebin/avdec)");
    return;
  }
  if (p->vProfile == PROFILE_INDEX_FALLBACK) {
    GstPad* sink = gst_element_get_static_pad (p->vhead, "sink");
    if (sink) {
      gst_pad_link_full (pad, sink, GST_PAD_LINK_CHECK_NOTHING);
      gst_object_unref (sink);
    }
    p->vchain_built = true;
    return;
  }
  const VideoProfile* prof = &g_profiles[p->vProfile];
  if (!prof || !profile_matches_caps (prof, caps)) {
    p->capsMismatch = true;
    LOG ("pad caps mismatch for profile %s -> next attempt", prof->name);
    return;
  }
  GstPad* sink = gst_element_get_static_pad (p->vhead, "sink");
  if (sink) {
    GstPadLinkReturn lr = gst_pad_link (pad, sink);
    if (lr != GST_PAD_LINK_OK) {
      lr = gst_pad_link_full (pad, sink, GST_PAD_LINK_CHECK_NOTHING);
      if (lr != GST_PAD_LINK_OK) {
        set_error (p, "pad->head link failed (%d)", lr);
        p->capsMismatch = true;
      }
    }
    p->vchain_built = (lr == GST_PAD_LINK_OK);
    if (p->vchain_built)
      record_video_caps (p, caps);
    /* 0.4.5-C: profiles without a parser (ProRes) expose keyframes on the
     * demux src pad only; parser-based profiles were probed at build time. */
    if (p->vchain_built && !prof->parse)
      install_gop_probe (p, pad);
    gst_object_unref (sink);
  }
}

static void
on_audio_bin_pad_added (GstElement* /*dbin*/, GstPad* pad, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  if (!p->aconvert)
    return;
  GstPad* sink = gst_element_get_static_pad (p->aconvert, "sink");
  if (sink) {
    gst_pad_link_full (pad, sink, GST_PAD_LINK_CHECK_NOTHING);
    gst_object_unref (sink);
  }
}

static gboolean
build_audio_chain (TcsPlayer* p, gboolean need_audio_decode)
{
  /* Audio tail built on the first audio pad (a statically placed empty
   * decodebin never completes the bin PAUSED transition and stalls video
   * preroll). The decoder output can be non-interleaved F32LE, which volume
   * rejects: the first audioconvert accepts it (S1 fix), the second
   * negotiates the sink format and audioresample converts to the device rate
   * (D12: a 44.1kHz source must not fail against a 48kHz shared-mode sink).
   * When autoaudiosink is unusable the sink is fakesink sync=true so video
   * playback is unaffected; the resampler stays in that path too. */
  p->aconvert = gst_element_factory_make ("audioconvert", nullptr);
  p->aqueue = gst_element_factory_make ("queue", nullptr);
  p->avolume = gst_element_factory_make ("volume", nullptr);
  p->aconvert2 = gst_element_factory_make ("audioconvert", nullptr);
  p->aresample = gst_element_factory_make ("audioresample", nullptr);
  bool is_fake = p->use_fakesink;
  GstElement* sink = nullptr;
  if (!is_fake)
    sink = gst_element_factory_make ("autoaudiosink", nullptr);
  if (!sink) {
    sink = gst_element_factory_make ("fakesink", nullptr);
    is_fake = true;
  }
  p->asink = sink;
  if (!p->aconvert || !p->aqueue || !p->avolume || !p->aconvert2 || !p->aresample || !p->asink)
    return FALSE;
  if (is_fake)
    g_object_set (p->asink, "sync", TRUE, nullptr);
  GstElement* ahead = nullptr;
  if (need_audio_decode) {
    p->adecodebin = gst_element_factory_make ("decodebin", nullptr);
    if (!p->adecodebin)
      return FALSE;
    ahead = p->adecodebin;
  } else {
    p->adecodebin = nullptr;
    ahead = p->aconvert;
  }
  g_object_set (p->avolume, "volume",
      (p->muted || p->load_priming || p->pump_muted) ? 0.0 : p->volume_value / 100.0, nullptr);
  if (p->adecodebin) {
    gst_bin_add (GST_BIN (p->pipeline), p->adecodebin);
    g_signal_connect (p->adecodebin, "pad-added",
        G_CALLBACK (on_audio_bin_pad_added), p);
  }
  gst_bin_add_many (GST_BIN (p->pipeline), p->aconvert, p->aqueue, p->avolume,
      p->aconvert2, p->aresample, p->asink, nullptr);
  if (!gst_element_link_many (p->aconvert, p->aqueue, p->avolume,
        p->aconvert2, p->aresample, p->asink, nullptr)) {
    set_error (p, "audio tail link failed");
    return FALSE;
  }
  p->ahead = ahead;
  GstPad* asinkpad = gst_element_get_static_pad (p->asink, "sink");
  if (asinkpad) {
    gst_pad_add_probe (asinkpad, GST_PAD_PROBE_TYPE_BUFFER,
        on_audio_sink_probe, p, nullptr);
    gst_object_unref (asinkpad);
  }
  return TRUE;
}

/* container by extension; unknown -> decodebin (full autoplugg, raw video pad) */
static const char*
select_demux_for_path (const char* path)
{
  const char* dot = strrchr (path, '.');
  if (!dot) return "decodebin";
  if (!_stricmp (dot, ".mp4") || !_stricmp (dot, ".mov") || !_stricmp (dot, ".m4v") ||
      !_stricmp (dot, ".3gp") || !_stricmp (dot, ".3g2"))
    return "qtdemux";
  if (!_stricmp (dot, ".mkv") || !_stricmp (dot, ".webm") || !_stricmp (dot, ".mka"))
    return "matroskademux";
  if (!_stricmp (dot, ".ts") || !_stricmp (dot, ".m2ts") || !_stricmp (dot, ".mts"))
    return "tsdemux";
  if (!_stricmp (dot, ".mxf"))
    return "mxfdemux";
  if (!_stricmp (dot, ".avi"))
    return "avidemux";
  return "decodebin";
}

static gboolean
demux_is_decodebin (const char* name)
{
  return g_strcmp0 (name, "decodebin") == 0;
}
static void
on_demux_pad_added (GstElement* /*demux*/, GstPad* pad, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  /* method 2: the first SEGMENT pushed after a TS seek carries the snapped
   * start used for the clock rebase */
  gst_pad_add_probe (pad, GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM,
      on_demux_segment_probe, p, nullptr);
  GstCaps* caps = gst_pad_get_current_caps (pad);
  if (!caps)
    caps = gst_pad_query_caps (pad, nullptr);
  const char* mt = caps_first_media (caps);
  gboolean is_video = mt && g_str_has_prefix (mt, "video/");
  gboolean is_audio = mt && g_str_has_prefix (mt, "audio/");
  LOG ("demux pad '%s' caps=%s", GST_PAD_NAME (pad), mt ? mt : "(none)");
  if (is_video)
    on_video_pad (p, pad, caps);
  else if (is_audio && p->audioEnabled) {
    if (!p->ahead) {
      /* first audio pad: build the audio chain now and sync it to the
       * current pipeline state (a statically placed empty decodebin never
       * completes the bin PAUSED transition and stalls video preroll) */
      if (!build_audio_chain (p, p->needAudioDecode)) {
        LOG ("audio chain build failed; falling back to a bare fakesink");
        if (!p->asink) {
          p->asink = gst_element_factory_make ("fakesink", nullptr);
          if (p->asink) {
            g_object_set (p->asink, "sync", TRUE, nullptr);
            gst_bin_add (GST_BIN (p->pipeline), p->asink);
            gst_element_sync_state_with_parent (p->asink);
          }
        }
        p->ahead = p->asink;
      }
      if (p->adecodebin)
        gst_element_sync_state_with_parent (p->adecodebin);
      if (p->aconvert)
        gst_element_sync_state_with_parent (p->aconvert);
      if (p->aqueue)
        gst_element_sync_state_with_parent (p->aqueue);
      if (p->avolume)
        gst_element_sync_state_with_parent (p->avolume);
      if (p->aconvert2)
        gst_element_sync_state_with_parent (p->aconvert2);
      if (p->aresample)
        gst_element_sync_state_with_parent (p->aresample);
      if (p->asink)
        gst_element_sync_state_with_parent (p->asink);
    }
    if (p->ahead) {
      GstPad* sink = gst_element_get_static_pad (p->ahead, "sink");
      if (sink) {
        GstPadLinkReturn lr = gst_pad_link_full (pad, sink, GST_PAD_LINK_CHECK_NOTHING);
        if (lr != GST_PAD_LINK_OK)
          LOG ("audio pad -> head link failed (%d)", lr);
        gst_object_unref (sink);
      }
    }
  }
  if (caps)
    gst_caps_unref (caps);
}
/* ---------------- D2: paused-seek preroll pump ---------------- */

/* Upper bound for one pump (D24: pump_budget_ms, default 4000ms). Reached
 * without a frame -> back to PAUSED and the failure is recorded (last_error +
 * counter + a warning log with the decode progress), never a silent PLAYING. */

/* Arm the pump for a seek that was issued while paused. Called outside
 * frame_lock / state_mutex by tcs_player_seek; returns immediately (the
 * PLAYING state change itself is asynchronous). */
static void
pump_arm (TcsPlayer* p, uint64_t generation)
{
  GstElement* pipeline = nullptr;
  {
    std::lock_guard<std::mutex> st (p->state_mutex);
    {
      std::lock_guard<std::mutex> g (p->frame_lock);
      if (!p->pipeline || !p->paused)
        return;
      p->pump_active = true;
      p->pump_pending.store (true, std::memory_order_relaxed);
      p->pump_generation = generation;
      p->pump_armed_ms = GetTickCount64 ();
      p->pump_deadline = p->pump_armed_ms + pump_budget_ms;
      p->pump_frames_at_arm = p->frames_decoded;
      if (!p->pump_muted) {
        /* No audible output while the pipeline runs for the preroll: the user
         * still believes playback is paused (same idea as load_priming). */
        p->pump_muted = true;
        apply_volume_locked (p);
      }
      pipeline = p->pipeline;
    }
    if (pipeline) {
      LOG ("pump_arm: set_state(PLAYING) begin");
      gst_element_set_state (pipeline, GST_STATE_PLAYING);
      LOG ("pump_arm: set_state(PLAYING) end");
    }
  }
}

/* Bus-thread tick: deliver the pending frame, then stop again. */
static void
pump_preroll_tick (TcsPlayer* p)
{
  if (!p || !p->pump_pending.load (std::memory_order_relaxed))
    return;
  GstElement* pipeline = nullptr;
  bool has_frame = false, timed_out = false, user_paused = false;
  uint64_t gen = 0, faults = 0;
  uint64_t target_ns = 0, decoded = 0, elapsed_ms = 0;
  {
    std::lock_guard<std::mutex> st (p->state_mutex);
    {
      std::lock_guard<std::mutex> g (p->frame_lock);
      if (!p->pump_active) {
        p->pump_pending.store (false, std::memory_order_relaxed);
        return;
      }
      for (const TcsPlayer::FrameSlot& f : p->frames) {
        if (f.generation == p->pump_generation) {
          has_frame = true;
          break;
        }
      }
      if (!has_frame && GetTickCount64 () >= p->pump_deadline)
        timed_out = true;
      if (!has_frame && !timed_out)
        return;
      p->pump_active = false;
      p->pump_pending.store (false, std::memory_order_relaxed);
      if (p->pump_muted) {
        p->pump_muted = false;
        apply_volume_locked (p);
      }
      user_paused = p->paused;
      gen = p->pump_generation;
      if (timed_out) {
      p->pump_faults++;
      faults = p->pump_faults;
      target_ns = p->gate_target_ns;
      decoded = p->frames_decoded >= p->pump_frames_at_arm
          ? p->frames_decoded - p->pump_frames_at_arm : 0;
      elapsed_ms = GetTickCount64 () - p->pump_armed_ms;
      p->last_error = "paused seek: no frame before the pump deadline";
      /* D25: no kept frame arrived within the budget; the seek's segment may
       * never have reached the appsink pad. Stop filtering so a later frame
       * is not dropped forever. */
      p->seek_boundary_expect.store (0, std::memory_order_release);
      }
      pipeline = p->pipeline;
    }
    if (timed_out) {
      /* D24: long-GOP seeks decode from the previous keyframe; the timeout is
       * a diagnostic, not a stream error. Report how far the decode got (the
       * distance left is target - position) instead of failing the seek. The
       * position query runs outside frame_lock (see I13's rule for the
       * state/seek calls it applies to; a query must not hold the lock while
       * the streaming thread may be waiting for it). */
      gint64 pos = -1;
      gboolean pos_ok = FALSE;
      if (pipeline)
        pos_ok = gst_element_query_position (pipeline, GST_FORMAT_TIME, &pos);
      LOG ("paused-seek: pump deadline gen=%llu -> PAUSED (faults=%llu "
          "target_ms=%.1f decoded=%llu elapsed_ms=%llu budget_ms=%llu "
          "position_ms=%.1f pos_ok=%d)",
          (unsigned long long) gen, (unsigned long long) faults,
          (double) target_ns / 1e6, (unsigned long long) decoded,
          (unsigned long long) elapsed_ms, (unsigned long long) pump_budget_ms,
          pos_ok ? (double) pos / 1e6 : -1.0, pos_ok ? 1 : 0);
    }
    /* A user resume clears pump_active in set_paused and keeps PLAYING. */
    if (pipeline && (timed_out || user_paused)) {
      LOG ("pump_tick: set_state(PAUSED) begin");
      gst_element_set_state (pipeline, GST_STATE_PAUSED);
      LOG ("pump_tick: set_state(PAUSED) end");
    }
  }
}

/* Drop pump state without touching the pipeline (teardown holds frame_lock). */
static void
pump_reset_locked (TcsPlayer* p)
{
  p->pump_active = false;
  p->pump_muted = false;
  p->pump_pending.store (false, std::memory_order_relaxed);
}

/* Apply the EOS restart that seek_locked deferred (pending_play_restart).
 * Must not be called with frame_lock held. */
static void
apply_pending_play_restart (TcsPlayer* p)
{
  GstElement* pipeline = nullptr;
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    if (!p->pending_play_restart)
      return;
    p->pending_play_restart = false;
    pipeline = p->pipeline;
  }
  if (pipeline)
    gst_element_set_state (pipeline, GST_STATE_PLAYING);
}

/* ---------------- bus thread ---------------- */

static void
handle_bus_message (TcsPlayer* p, GstMessage* msg)
{
  switch (GST_MESSAGE_TYPE (msg)) {
    case GST_MESSAGE_ERROR: {
      GError* e = nullptr;
      gst_message_parse_error (msg, &e, nullptr);
      std::lock_guard<std::mutex> g (p->frame_lock);
      p->failed = true;
      p->last_error = e ? e->message : "stream error";
      p->last_bus_error = p->last_error;
      LOG ("bus error: %s", p->last_error.c_str ());
      if (e)
        g_error_free (e);
      break;
    }
    case GST_MESSAGE_EOS: {
      std::lock_guard<std::mutex> g (p->frame_lock);
      p->eos = true;
      LOG ("bus EOS");
      break;
    }
    default:
      break;
  }
}

static void
bus_loop (TcsPlayer* p)
{
  while (p->bus_running.load ()) {
    pump_preroll_tick (p);
    GstBus* bus = p->pipeline ? gst_element_get_bus (p->pipeline) : nullptr;
    if (bus) {
      GstMessage* msg = gst_bus_timed_pop_filtered (bus, 2 * GST_MSECOND,
          (GstMessageType) (GST_MESSAGE_ERROR | GST_MESSAGE_EOS));
      gst_object_unref (bus);
      if (msg) {
        handle_bus_message (p, msg);
        gst_message_unref (msg);
      }
    } else {
      Sleep (2);
    }
  }
}

/* ---------------- pipeline lifecycle ---------------- */

static void
teardown_pipeline (TcsPlayer* p)
{
  p->bus_running = false;
  if (p->bus_thread.joinable ())
    p->bus_thread.join ();

  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    for (TcsPlayer::FrameSlot& slot : p->frames)
      gst_sample_unref (slot.sample);
    p->frames.clear ();
    if (p->leased) { gst_sample_unref (p->leased); p->leased = nullptr; }
    p->leased_slot = -1;
    p->pending_update = false;
    p->frames_decoded = 0;
    /* D25: a new pipeline sends its own initial segment; no seek is pending. */
    p->seek_boundary_expect.store (0, std::memory_order_release);
    p->gate_active = false;
    p->gate_dropped = 0;
    p->rebase_armed = false;
    p->rebase_seek_seqnum = 0;
    p->av_log_left = 0;
    p->pending_play_restart = false;
    pump_reset_locked (p);
  }

  /* 0.4.5-C: invalidate probe contexts before the old pipeline goes away and
   * clear the measurement outside frame_lock (gop_lock is a leaf, never
   * nested with frame_lock). */
  p->gop_epoch.fetch_add (1, std::memory_order_acq_rel);
  {
    std::lock_guard<std::mutex> g (p->gop_lock);
    tcs_gop_tracker_reset (&p->gop_tracker);
    p->gop_active = false;
    p->gop_probe_installed = false;
  }
  p->gop_reanchor.store (false, std::memory_order_release);

  if (p->pipeline) {
    gst_element_set_state (p->pipeline, GST_STATE_NULL);
    gst_object_unref (p->pipeline);
    p->pipeline = nullptr;
  }
  p->demux = nullptr;
  p->ahead = nullptr;
  p->appsink = nullptr;
  p->vcaps = nullptr;
  p->vhead = nullptr;
  p->vqueue = nullptr;
  p->vparse = nullptr;
  p->vdec = nullptr;
  p->vconvert = nullptr;
  p->vupload = nullptr;
  p->vgpuconvert = nullptr;
  p->vchain_built = false;
  p->capsMismatch = false;
  p->aqueue = nullptr;
  p->avolume = nullptr;
  p->aconvert = nullptr;
  p->aconvert2 = nullptr;
  p->aresample = nullptr;
  p->asink = nullptr;
  p->adecodebin = nullptr;
  p->video_rewrite_installed = false;
  p->audio_rewrite_sink = nullptr;
}

/* A seek prepared under frame_lock. The event/parameters are sent by
 * seek_send() AFTER the lock is released: a flushing seek needs the streaming
 * thread, which may be inside on_new_sample waiting for frame_lock while
 * holding the sink's stream lock (the rule documented in
 * tcs_player_set_paused). Holding frame_lock across the send deadlocks. */
struct SeekRequest {
  bool valid = false;
  bool keyunit = false;        /* keyunit method: gate + segment rewrite + event */
  bool mpegts = false;         /* container (diagnostic text only) */
  GstElement* pipeline = nullptr;
  double seconds = 0.0;
  double rate = 1.0;
  gint64 target_ns = 0;
  GstSeekFlags flags = (GstSeekFlags) 0;
  guint32 seq = 0;
  GstEvent* event = nullptr;   /* keyunit: created under frame_lock, sent after */
};

/* Seek preparation (manual seek, step and load-with-start all go through
 * this): opens a new generation, discards undelivered frames and arms the TS
 * delivery gate. Caller holds frame_lock. The seek itself is NOT sent here;
 * the caller runs seek_send() after releasing frame_lock. */
static uint64_t
seek_prepare_locked (TcsPlayer* p, double seconds, double rate, SeekRequest* out)
{
  out->valid = false;
  if (!p->pipeline)
    return p->generation;
  p->generation++;
  /* 0.4.5-C: a seek can jump the PTS far ahead; the probe re-anchors on the
   * next keyframe so the jump cannot look like a long keyframe gap. The
   * latched warning is kept (per-track, monotone). */
  p->gop_reanchor.store (true, std::memory_order_release);
  /* D25: this seek's segment is the next downstream SEGMENT event, so the
   * expected boundary is "seen + 1". Back-to-back seeks are deliberately not
   * stacked (the earlier max(seen, pending)+1): when two flushing seeks
   * collapse into one segment event (observed with consecutive paused seeks:
   * the second segment never reaches the appsink pad), a stacked expectation
   * never materializes and every later frame is dropped forever. With seen+1
   * the worst case is that the earlier seek's frame is published as the new
   * generation's first frame, which the owner's landing gate rejects and
   * re-seeks (D21-b); the time bound in sample_is_pre_seek covers a segment
   * that never arrives at all. */
  {
    uint64_t seen = p->flush_boundary.load (std::memory_order_acquire);
    p->seek_boundary_expect.store (seen + 1, std::memory_order_release);
    p->seek_expect_ms.store (GetTickCount64 (), std::memory_order_relaxed);
  }
  /* frames of the previous generation must never reach the compositor */
  for (TcsPlayer::FrameSlot& slot : p->frames)
    gst_sample_unref (slot.sample);
  p->frames.clear ();
  p->pending_update = false;
  p->gate_target_ns = (uint64_t) (seconds * (double) GST_SECOND);
  /* A/V diagnostics window (TCS_SEEK_DIAG=1): the gate-open frame is always
   * logged, the decimated window follows the seek for both TS and MP4. */
  p->av_log_left = env_flag ("TCS_SEEK_DIAG") ? 900 : 0;
  p->av_log_stride = 15;
  p->av_log_seq = p->frames_decoded;
  if (p->eos) {
    p->eos = false;
    if (!p->paused)
      p->pending_play_restart = true;
  }
  if (test_hold_seek_lock_ms > 0 && test_seek_hold_budget.fetch_sub (1) > 0)
    Sleep ((DWORD) test_hold_seek_lock_ms);
  /* C1(b): the seek method is a measurement switch. auto keeps the container
   * default: tsdemux's ACCURATE seek scans each PES for a keyframe NAL and
   * loses track on H.264 whose IDRs carry no SPS/PPS, so TS snaps to the
   * keyframe before the target (KEY_UNIT|SNAP_BEFORE) and the sinks drop the
   * pre-target samples (method 2 segment rewrite; the delivery gate is the
   * fallback). MP4/MOV and the rest keep the accurate seek (their landing
   * error is already one frame at most). accurate/keyunit force one method
   * for both containers; the gate and the segment rewrite are armed only for
   * keyunit, so a forced accurate seek on TS leaves neither armed. */
  bool keyunit = seek_method == kSeekMethodKeyUnit ||
      (seek_method == kSeekMethodAuto && p->mpegts);
  GstSeekFlags flags = keyunit
      ? (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_KEY_UNIT |
          GST_SEEK_FLAG_SNAP_BEFORE)
      : (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE);
  out->keyunit = keyunit;
  out->mpegts = p->mpegts;
  if (keyunit) {
    LARGE_INTEGER now;
    QueryPerformanceCounter (&now);
    p->gate_active = true;
    p->gate_target_ns = (uint64_t) (seconds * (double) GST_SECOND);
    p->gate_dropped = 0;
    p->gate_armed_qpc = (uint64_t) now.QuadPart;
    p->gate_first_qpc = 0;
    p->gate_first_pts_ns = 0;
    /* method 2: the sinks rewrite the next segment (start = target) so the
     * pipeline prerolls on the target; the seek seqnum identifies it. */
    p->rebase_armed = true;
    ensure_sink_segment_rewrites (p);
    p->seg_diag_left = env_flag ("TCS_SEEK_DIAG") ? 5 : 0;
    p->gate_diag_done = false;
    guint32 seq = gst_util_seqnum_next ();
    GstEvent* sev = gst_event_new_seek (rate > 0.0 ? rate : 1.0,
        GST_FORMAT_TIME, flags, GST_SEEK_TYPE_SET,
        (gint64) (seconds * GST_SECOND), GST_SEEK_TYPE_NONE, -1);
    gst_event_set_seqnum (sev, seq);
    /* TCS_NO_SEGMENT_REWRITE=1 is a debug escape hatch back to method 5 */
    p->rebase_seek_seqnum = env_flag ("TCS_NO_SEGMENT_REWRITE") ? 0 : seq;
    out->event = sev;
    out->seq = seq;
  } else {
    /* The gate and the rewrite must not stay armed for an accurate seek
     * (forcing accurate on TS would otherwise corrupt the measurement). */
    p->gate_active = false;
    p->rebase_armed = false;
    p->rebase_seek_seqnum = 0;
  }
  out->valid = true;
  out->pipeline = p->pipeline;
  out->seconds = seconds;
  out->rate = rate > 0.0 ? rate : 1.0;
  out->target_ns = (gint64) (seconds * GST_SECOND);
  out->flags = flags;
  return p->generation;
}

/* Send a prepared seek with NO lock held (see SeekRequest). Returns false when
 * GStreamer rejected it (0.4.6: callers used to ignore that and report the seek
 * as issued). An invalid request (no pipeline) is not a rejection. */
static bool
seek_send (TcsPlayer* p, const SeekRequest& req)
{
  if (!req.valid || !req.pipeline)
    return true;
  gboolean ok;
  if (req.keyunit) {
    LOG ("seek: send begin (ts) method=keyunit container=%s target_ns=%llu seq=%u",
        req.mpegts ? "ts" : "other", (unsigned long long) req.target_ns, req.seq);
    ok = gst_element_send_event (req.pipeline, req.event);
    LOG ("seek: send end (ts) method=keyunit ok=%d", ok ? 1 : 0);
    LOG ("seek: %s keyframe-snap target_ns=%llu gate armed seq=%u",
        req.mpegts ? "ts" : "keyunit", (unsigned long long) req.target_ns, req.seq);
  } else {
    LOG ("seek: send begin (accurate) method=accurate container=%s target_ns=%llu",
        req.mpegts ? "ts" : "other", (unsigned long long) req.target_ns);
    ok = gst_element_seek (req.pipeline, req.rate, GST_FORMAT_TIME, req.flags,
        GST_SEEK_TYPE_SET, req.target_ns, GST_SEEK_TYPE_NONE, -1);
    LOG ("seek: send end (accurate) method=accurate ok=%d", ok ? 1 : 0);
  }
  if (!ok) {
    char msg[256];
    snprintf (msg, sizeof (msg),
        "seek failed (gst_element_seek FALSE, %s, target=%.3f)",
        req.keyunit ? "keyunit/snap-before" : "accurate", req.seconds);
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->last_error = msg;
    /* D25: no segment will arrive for a failed seek; stop filtering. */
    p->seek_boundary_expect.store (0, std::memory_order_release);
    LOG ("%s", msg);
  }
  return ok != FALSE;
}

static int
build_pipeline (TcsPlayer* p, const char* utf8_path, double start_sec, int paused)
{
  const uint64_t t_load = qpc_now ();
  {
    /* O1: the appended bus error must belong to this load, not a previous one. */
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->last_bus_error.clear ();
  }
  const char* demux_name = select_demux_for_path (utf8_path);
  gboolean ext_is_decodebin = demux_is_decodebin (demux_name);

  int order[kProfileCount + 1];
  int software_flags[kProfileCount];
  for (int i = 0; i < kProfileCount; i++)
    software_flags[i] = profile_is_software (i) ? 1 : 0;
  /* The full order (profiles and the decodebin fallback) is written here in
   * one call; do not append to `order` afterwards. */
  int nOrder = tcs_decode_profile_order (
      ext_is_decodebin ? 1 : 0,
      p->decode_mode == TCS_DECODE_MODE_SOFTWARE ? 1 : 0,
      p->lastGoodProfile, software_flags, kProfileCount, order);

  for (int attempt = 0; attempt < nOrder; attempt++) {
    int idx = order[attempt];
    const char* container = (idx == PROFILE_INDEX_FALLBACK) ? "decodebin" : demux_name;
    /* v0.5.0: この試行で組む経路の名前。HAP だと分かっている試行（2 回目以降）は hap の連鎖で組むので
     * hap-gpu、それ以外は番号上のプロファイル名。試行の途中で HAP と分かっても、その試行は元の名前のまま
     * （組もうとした経路の名前）。load.attempt と load.summary で同じ名前を使う（試験側が突き合わせる）。 */
    const char* attempt_profile = p->hap_stream ? "hap-gpu"
        : idx >= 0 ? g_profiles[idx].name : "decodebin-fallback";
    if (p->hap_stream && idx == PROFILE_INDEX_FALLBACK) {
      /* v0.5.0: HAP に decodebin の最終手段は使わない（avdec_hap が CPU で展開してしまう）。 */
      LOG ("hap: skipping the decodebin fallback (never CPU-decode HAP)");
      continue;
    }

    /* S4 phase timing (QPC). Anchors are set as the load advances so a failed
     * attempt still reports where it stopped. */
    const uint64_t t_attempt0 = qpc_now ();
    uint64_t t_anchor = t_attempt0;
    double teardown_ms = 0, build_ms = 0, set_state_ms = 0, preroll_ms = 0;
    double first_frame_ms = 0, audio_prime_ms = 0, pause_ms = 0, seek_ms = 0;
    double duration_ms = 0;
    int64_t frames_at_frame = -1;
    auto log_attempt = [&] (const char* result) {
      uint64_t now = qpc_now ();
      LOG ("load.attempt path=%s paused=%d attempt=%d profile=%s result=%s "
          "teardown_ms=%.1f build_ms=%.1f set_state_ms=%.1f preroll_ms=%.1f "
          "first_frame_ms=%.1f audio_prime_ms=%.1f pause_ms=%.1f seek_ms=%.1f "
          "duration_ms=%.1f total_ms=%.1f frames=%lld",
          utf8_path, paused ? 1 : 0, attempt,
          attempt_profile, result,
          teardown_ms, build_ms, set_state_ms, preroll_ms, first_frame_ms,
          audio_prime_ms, pause_ms, seek_ms, duration_ms,
          qpc_diff_ms (t_attempt0, now, p->qpc_freq),
          (long long) frames_at_frame);
    };

    teardown_pipeline (p);
    teardown_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);
    t_anchor = qpc_now ();
    /* D16: a GPU profile whose decoder is not available on the shim adapter
     * would run on a foreign device (hybrid GPU). Skip it so the order falls
     * through to the CPU profile. */
    if (!adapter_supports_profile (p, idx)) {
      LUID luid = {};
      device_luid (p->device, &luid);
      LOG ("load.skip path=%s attempt=%d profile=%s reason=adapter-lacks-decoder "
          "adapter_luid=%08lx:%08lx",
          utf8_path, attempt, idx >= 0 ? g_profiles[idx].name : "decodebin-fallback",
          (unsigned long) luid.HighPart, (unsigned long) luid.LowPart);
      log_attempt ("skipped-adapter-unsupported");
      continue;
    }
    /* D16-b: the decoder class must be registered for the shim adapter; the
     * per-adapter variants are tried by the helper. */
    if (!decoder_matches_shim_adapter (p, idx, &p->decoder_element_name)) {
      LUID luid = {};
      device_luid (p->device, &luid);
      LOG ("load.skip path=%s attempt=%d profile=%s reason=decoder-adapter-mismatch "
          "shim_luid=%08lx:%08lx",
          utf8_path, attempt, idx >= 0 ? g_profiles[idx].name : "decodebin-fallback",
          (unsigned long) luid.HighPart, (unsigned long) luid.LowPart);
      log_attempt ("skipped-decoder-adapter");
      continue;
    }
    /* Method 5 applies to the tsdemux container only (other demuxers keep
     * the accurate seek). */
    p->mpegts = g_strcmp0 (container, "tsdemux") == 0;

    p->failed = false;
    p->eos = false;
    p->duration = -1.0;
    p->width = p->height = 0;
    p->fps = 0.0;
    p->use_d3d11_caps = FALSE;
    p->rejected = false;
    uint64_t attempt_gen = 0;
    {
      std::lock_guard<std::mutex> g (p->frame_lock);
      p->decoder_name.clear ();
      attempt_gen = ++p->generation;
      /* D34: a frame of the previous pipeline can land after teardown's reset
       * (teardown clears frames_decoded before the old pipeline reaches NULL).
       * Clear the counter again here; the generation gate below rejects any
       * late frame that still slips through. */
      p->frames_decoded = 0;
      p->pending_update = false;
    }

    p->pipeline = gst_pipeline_new ("tcs_play");
    /* Method 2 support: a flushing seek stops the wasapi2 ringbuffer and on
     * some systems it does not restart promptly, which freezes the clock the
     * audio sink provides. Every sink waiting on that clock then stalls (the
     * target frame never arrives) until the device recovers ~10s later.
     * Forcing the system clock keeps the pipeline clock alive; the audio sink
     * slaves to it instead (GstAudioBaseSink default skew slaving), so A/V
     * sync is kept. Measured: seek-to-lease 185ms, A/V offset ~-1ms. */
    {
      GstClock* sysclock = gst_system_clock_obtain ();
      gst_pipeline_use_clock (GST_PIPELINE (p->pipeline), sysclock);
      gst_object_unref (sysclock);
    }
    GstElement* src = gst_element_factory_make ("filesrc", nullptr);
    p->demux = gst_element_factory_make (container, nullptr);
    if (!p->pipeline || !src || !p->demux) {
      if (p->pipeline) { gst_object_unref (p->pipeline); p->pipeline = nullptr; }
      set_error (p, "element factory failed (demux %s)", container);
      log_attempt ("factory-fail");
      return TCS_ERR_GENERIC;
    }
    g_object_set (src, "location", utf8_path, nullptr);

    if (!build_video_chain_static (p, idx)) {
      log_attempt ("chain-fail");
      teardown_pipeline (p);
      continue;
    }
    /* audio tail is built on first audio pad-added (a static empty decodebin
     * never completes the bin PAUSED transition and stalls video preroll) */
    p->needAudioDecode = !demux_is_decodebin (container);

    gst_bin_add_many (GST_BIN (p->pipeline), src, p->demux, nullptr);
    if (!gst_element_link (src, p->demux)) {
      log_attempt ("src-demux-link-fail");
      teardown_pipeline (p);
      set_error (p, "src->demux link failed");
      return TCS_ERR_GENERIC;
    }
    g_signal_connect (p->demux, "pad-added", G_CALLBACK (on_demux_pad_added), p);
    give_device_context (p, p->pipeline);
    {
      GstBus* pbus = gst_element_get_bus (p->pipeline);
      gst_bus_set_sync_handler (pbus, sync_bus_handler, p, nullptr);
      gst_object_unref (pbus);
    }
    build_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);
    t_anchor = qpc_now ();

    /* A paused load primes the audio sink silently and only drops to PAUSED
     * after its first buffer: stopping wasapi2 mid-initialization leaves it
     * broken for the following PLAYING (frame clock never advances). */
    p->load_priming = (paused != 0) && p->audioEnabled;
    p->audio_sink_buffers.store (0, std::memory_order_relaxed);

    /* D14: run the bus thread during the state wait so a bus ERROR (an
     * unsupported profile) aborts this attempt at once instead of burning the
     * fixed 3 s + 3 s timeout per mismatched profile. */
    p->bus_running = true;
    p->bus_thread = std::thread (bus_loop, p);

    GstStateChangeReturn scr = gst_element_set_state (p->pipeline, GST_STATE_PLAYING);
    set_state_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);
    t_anchor = qpc_now ();
    if (scr == GST_STATE_CHANGE_FAILURE) {
      log_attempt ("set-state-fail");
      teardown_pipeline (p);
      continue;
    }
    /* The d3d11 decoder + video-processor converter only emit in PLAYING,
     * so we bring the pipeline up to PLAYING to obtain the first frame,
     * then drop back to PAUSED for a paused load (matches "first frame
     * visible while paused"). D14/D17-a: poll in 100 ms slices and leave as
     * soon as the bus reports an error, a pad caps mismatch is recorded, or
     * the target state is reached. */
    for (int i = 0; i < 30; i++) {
      GstStateChangeReturn sr =
          gst_element_get_state (p->pipeline, nullptr, nullptr, 100 * GST_MSECOND);
      if (sr != GST_STATE_CHANGE_ASYNC)
        break;
      bool stop_wait;
      {
        std::lock_guard<std::mutex> g (p->frame_lock);
        /* D17-a: pad-added decides a caps mismatch (and the policy rejection)
         * without a bus error; stop the wait at once instead of burning the
         * remaining 100 ms slices. */
        stop_wait = p->failed || p->capsMismatch || p->rejected;
      }
      if (stop_wait)
        break;
    }
    preroll_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);

    bool done = false;
    bool current_gen_frame = false;
    bool caps_ready = false;
    int wait_iters = env_flag ("TCS_ONE_ATTEMPT") ? 60 : 300;
    for (int i = 0; i < wait_iters; i++) {
      {
        std::lock_guard<std::mutex> g (p->frame_lock);
        /* D34: only a frame of THIS attempt's generation may satisfy the first
         * frame gate. A late frame of the previous pipeline (delivered while
         * teardown set the old pipeline to NULL) carries the old generation. */
        current_gen_frame = p->frames_decoded > 0 && p->latest_gen == attempt_gen;
        done = current_gen_frame || p->failed || p->capsMismatch || p->rejected;
        if (p->frames_decoded > 0)
          frames_at_frame = (int64_t) p->frames_decoded;
      }
      if (done)
        break;
      Sleep (50);
    }
    first_frame_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);
    {
      std::lock_guard<std::mutex> g (p->frame_lock);
      /* D34: width/height only. Variable-framerate containers may report
       * framerate=0/1; fps=0 is accepted (logged below) and the product keeps
       * its fps default. */
      caps_ready = tcs_load_caps_ready (p->width, p->height) != 0;
      /* D34: success requires a frame of this generation, determined video
       * caps (0x0@0 is not a loaded stream), no caps mismatch and no error.
       * A mismatched profile must never be recorded as last-good. */
      done = tcs_load_attempt_ok (current_gen_frame ? 1 : 0, caps_ready ? 1 : 0,
          p->capsMismatch ? 1 : 0, p->failed ? 1 : 0, p->rejected ? 1 : 0) != 0;
    }
    if (p->rejected) {
      log_attempt ("rejected");
      teardown_pipeline (p);
      set_error (p, "unsupported stream by policy: video/x-hap needs the reserved compressed-texture branch");
      return TCS_ERR_NOT_LOADED;
    }
    if (!done) {
      {
        GstState cs2, ps2;
        gst_element_get_state (p->pipeline, &cs2, &ps2, 0);
        LOG ("diag: pipe cur=%d pending=%d", (int) cs2, (int) ps2);
        GstElement* els[] = { p->vparse, p->vdec, p->vconvert, p->vupload,
            p->vgpuconvert, p->appsink };
        const char* nms[] = { "vparse", "vdec", "vconv", "vupload", "vgpuconv",
            "vsink" };
        for (int ei = 0; ei < 6; ei++) {
          if (!els[ei])
            continue;
          GstState s3 = GST_STATE_VOID_PENDING;
          gst_element_get_state (els[ei], &s3, nullptr, 0);
          GstPad* spd = gst_element_get_static_pad (els[ei], "src");
          GstCaps* cpd = spd ? gst_pad_get_current_caps (spd) : nullptr;
          gchar* cstr = cpd ? gst_caps_to_string (cpd) : g_strdup ("(none)");
          LOG ("  elem %s state=%d srccaps=%s", nms[ei], (int) s3, cstr);
          g_free (cstr);
          if (cpd) gst_caps_unref (cpd);
          if (spd) gst_object_unref (spd);
        }
      }
      std::string err;
      bool caps_mismatch;
      int load_w, load_h;
      double load_fps;
      {
        std::lock_guard<std::mutex> g (p->frame_lock);
        err = p->last_error;
        caps_mismatch = p->capsMismatch;
        load_w = p->width;
        load_h = p->height;
        load_fps = p->fps;
      }
      /* D34: an attempt without determined caps (or with a pad caps mismatch)
       * is reported as caps-missing so the order advances; the success gate
       * above already keeps last_good untouched. */
      const bool caps_missing =
          tcs_load_failure_is_caps_missing (caps_ready ? 1 : 0, caps_mismatch ? 1 : 0) != 0;
      log_attempt (caps_missing ? "caps-missing" : "preroll-timeout");
      if (caps_missing)
        LOG ("load.caps-missing path=%s attempt=%d profile=%s mismatch=%d "
            "width=%d height=%d fps=%.3f current_gen_frame=%d",
            utf8_path, attempt, idx >= 0 ? g_profiles[idx].name : "decodebin-fallback",
            caps_mismatch ? 1 : 0, load_w, load_h, load_fps,
            current_gen_frame ? 1 : 0);
      teardown_pipeline (p);
      LOG ("attempt %s/%s failed: %s", container,
          idx >= 0 ? g_profiles[idx].name : "decodebin-fallback",
          err.empty () ? "preroll timeout / caps mismatch" : err.c_str ());
      if (env_flag ("TCS_ONE_ATTEMPT")) break;
      continue;
    }

    t_anchor = qpc_now ();
    if (start_sec > 0.0) {
      SeekRequest req;
      {
        std::lock_guard<std::mutex> g (p->frame_lock);
        p->eos = false;
        /* same seek semantics (and TS gate) as a manual seek; a load seek
         * always starts at normal rate like before */
        seek_prepare_locked (p, start_sec, 1.0, &req);
      }
      seek_send (p, req);
    }
    seek_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);
    t_anchor = qpc_now ();

    p->path = utf8_path;
    p->paused = paused != 0;
    p->lastGoodProfile = idx;
    /* The rebuilt pipeline starts at rate 1.0 (the seek above used 1.0). Keep
     * the saved value in sync so a later coarse seek cannot reuse a rate left
     * over from a correction that the owner already reset. Plain scalar write,
     * control thread only; no GStreamer call here. */
    p->rate = 1.0;
    if (p->decode_mode == TCS_DECODE_MODE_SOFTWARE && idx >= 0 && !profile_is_software (idx))
      LOG ("software decode requested but no CPU decoder was usable; "
          "using hardware profile %s", g_profiles[idx].name);

    if (paused) {
      /* wait (bounded) for the audio sink to consume its first buffer before
       * pausing; otherwise the sink never recovers on the next PLAYING. */
      if (p->asink) {
        LOG ("load.prime: waiting for the audio sink (frames=%llu)",
            (unsigned long long) p->frames_decoded);
        for (int i = 0; i < 40; i++) {
          bool failed;
          {
            std::lock_guard<std::mutex> g (p->frame_lock);
            failed = p->failed;
          }
          if (failed || p->audio_sink_buffers.load (std::memory_order_relaxed) > 0)
            break;
          Sleep (50);
        }
        LOG ("load.prime: done buffers=%llu",
            (unsigned long long) p->audio_sink_buffers.load (std::memory_order_relaxed));
      }
      audio_prime_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);
      t_anchor = qpc_now ();
      /* The PAUSED transition must NOT run under frame_lock: the streaming
       * thread can be inside on_new_sample waiting for frame_lock while
       * holding the sink's stream lock, and the state change needs that
       * stream lock (the rule documented in tcs_player_set_paused). Holding
       * frame_lock here deadlocks load: seek-to-lease never returns. */
      LOG ("load.pause: set_state(PAUSED) begin");
      gst_element_set_state (p->pipeline, GST_STATE_PAUSED);
      LOG ("load.pause: set_state(PAUSED) end");
      pause_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);
      t_anchor = qpc_now ();
      p->load_priming = false;
      if (p->avolume)
        g_object_set (p->avolume, "volume",
            p->muted ? 0.0 : p->volume_value / 100.0, nullptr);
    } else {
      p->load_priming = false;
    }

    gint64 q = 0;
    if (gst_element_query_duration (p->pipeline, GST_FORMAT_TIME, &q) && q > 0)
      p->duration = (double) q / GST_SECOND;
    duration_ms = qpc_diff_ms (t_anchor, qpc_now (), p->qpc_freq);

    log_attempt ("ok");
    if (p->fps <= 0.0)
      LOG ("load.fps-missing path=%s profile=%s fps=0 (framerate absent in caps) accepted",
          utf8_path, idx >= 0 ? g_profiles[idx].name : "decodebin-fallback");
    LOG ("loaded (%s / profile %d) %s decoder=%s %dx%d@%.3f mem=%s", container, idx,
        utf8_path, p->decoder_name.empty () ? "?" : p->decoder_name.c_str (),
        p->width, p->height, p->fps, p->use_d3d11_caps ? "d3d11" : "sysmem");
    LOG ("load.summary path=%s paused=%d total_ms=%.1f attempt=%d profile=%s",
        utf8_path, paused ? 1 : 0,
        qpc_diff_ms (t_load, qpc_now (), p->qpc_freq), attempt, attempt_profile);
    return TCS_OK;
  }

  std::string last_bus;
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    last_bus = p->last_bus_error;
  }
  if (last_bus.empty ())
    set_error (p, "all video profiles failed for %s", utf8_path);
  else
    set_error (p, "all video profiles failed for %s (last: %s)", utf8_path, last_bus.c_str ());
  return TCS_ERR_NOT_LOADED;
}

/* ---------------- D3D helpers (frame_lock held) ---------------- */

/* Borrowed texture of the current lease; valid until tcs_player_release().
 * Ring lease (slot >= 0): the shared ring texture (shim-owned). Legacy sample
 * lease: the pool texture (kept alive by the sample ref) or, for array
 * textures, the player-owned flattened single_tex. The caller must NOT
 * Release the returned pointer. */
static ID3D11Texture2D*
texture_of_lease (TcsPlayer* p, guint* sub_out)
{
  *sub_out = 0;
  if (p->leased_slot >= 0 && p->leased_slot < (int32_t) TcsPlayer::kRingSlots) {
    /* D8: an old-epoch ring lease refers to a ring the shim already rebuilt.
     * The compositor draws it from its own opened resources, so the shim has
     * no texture to hand out for it. */
    if (p->lease_info.ring_epoch != p->ring_epoch)
      return nullptr;
    return p->ring_texture[p->leased_slot];
  }
  if (!p->leased)
    return nullptr;
  GstBuffer* buf = gst_sample_get_buffer (p->leased);
  GstMemory* mem = buf ? gst_buffer_peek_memory (buf, 0) : nullptr;
  if (!mem || !gst_is_d3d11_memory (mem))
    return nullptr;
  GstD3D11Memory* dmem = (GstD3D11Memory*) mem;
  ID3D11Resource* res = gst_d3d11_memory_get_resource_handle (dmem);
  guint sub = gst_d3d11_memory_get_subresource_index (dmem);
  if (!res)
    return nullptr;
  ID3D11Texture2D* tex = nullptr;
  if (FAILED (res->QueryInterface (__uuidof(ID3D11Texture2D), (void**) &tex)) || !tex)
    return nullptr;
  D3D11_TEXTURE2D_DESC desc;
  tex->GetDesc (&desc);
  if (desc.ArraySize == 1 && sub == 0) {
    tex->Release ();  /* borrowed: the leased sample pins the pool texture */
    *sub_out = sub;
    return tex;
  }
  /* array texture: flatten into our single texture (player-owned) */
  if (!p->single_tex ||
      p->single_desc.Width != desc.Width ||
      p->single_desc.Height != desc.Height ||
      p->single_desc.Format != desc.Format) {
    if (p->single_tex) { p->single_tex->Release (); p->single_tex = nullptr; }
    D3D11_TEXTURE2D_DESC d = desc;
    d.ArraySize = 1;
    d.MipLevels = 1;
    if (FAILED (p->device->CreateTexture2D (&d, nullptr, &p->single_tex))) {
      tex->Release ();
      return nullptr;
    }
    p->single_desc = d;
  }
  D3D11_BOX box = {};
  box.right = desc.Width;
  box.bottom = desc.Height;
  box.back = 1;
  p->context->CopySubresourceRegion (p->single_tex, 0, 0, 0, 0, tex, sub, &box);
  p->context->Flush ();
  tex->Release ();
  *sub_out = 0;
  return p->single_tex;
}

/* ---------------- public API ---------------- */

extern "C" {

TCS_GST_API TcsPlayer*
tcs_player_create (const char* sender_name, void* external_d3d11_device,
                   char* errbuf, size_t errbuf_len)
{
  std::call_once (g_gst_once, [] {
    int argc = 0;
    gst_init (&argc, nullptr);
  });

  TcsPlayer* p = new TcsPlayer ();
  p->sender_name = (sender_name && sender_name[0]) ? sender_name : "TimecodeSyncPlayer";
  p->position_trace = env_flag (kOutputTraceEnv);
  LARGE_INTEGER qpc_freq;
  if (QueryPerformanceFrequency (&qpc_freq))
    p->qpc_freq = qpc_freq.QuadPart;
  tcs_gop_tracker_init (&p->gop_tracker, resolve_gop_warn_seconds ());

  demote_foreign_gpu_decoders ();
  p->audioEnabled = !env_flag ("TCS_NO_AUDIO");
  p->use_fakesink = p->audioEnabled &&
      (env_flag ("TCS_FAKE_AUDIO") || !audio_sink_usable ());
  LOG ("audio: %s", !p->audioEnabled ? "disabled (TCS_NO_AUDIO)"
      : (p->use_fakesink ? "autoaudiosink unusable -> fakesink sync=true" : "autoaudiosink"));

  if (!create_or_adopt_device (p, (ID3D11Device*) external_d3d11_device)) {
    if (errbuf && errbuf_len)
      snprintf (errbuf, errbuf_len, "%s", p->last_error.c_str ());
    if (p->gst_dev) gst_object_unref (p->gst_dev);
    if (p->context4) p->context4->Release ();
    if (p->device5) p->device5->Release ();
    if (p->context) p->context->Release ();
    if (p->device) p->device->Release ();
    delete p;
    return nullptr;
  }

  p->spout = new spoutDX ();
  if (!p->spout->OpenDirectX11 (p->device)) {
    LOG ("SpoutDX unavailable; verification publishing disabled");
    delete p->spout;
    p->spout = nullptr;
  } else {
    p->spout->SetSenderName (p->sender_name.c_str ());
    p->spout_ready = true;
  }
  return p;
}

TCS_GST_API void
tcs_player_destroy (TcsPlayer* player)
{
  if (!player)
    return;
  TcsPlayer* p = player;
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->notify = nullptr;
    p->notify_user = nullptr;
  }
  teardown_pipeline (p);
  /* ring handles belong to the shim (CreateSharedHandle); close them before
   * the device goes away. The compositor's opened references stay alive. */
  if (p->ring_ready)
    LOG ("ring: destroyed %dx%d BGRA slots=%u lease_outstanding=%d",
        p->ring_width, p->ring_height, TcsPlayer::kRingSlots,
        (p->leased || p->leased_slot >= 0) ? 1 : 0);
  destroy_ring (p);
  if (p->hap_gpu) { tcs_hap_gpu_destroy (p->hap_gpu); p->hap_gpu = nullptr; }
  if (p->single_tex) p->single_tex->Release ();
  if (p->spout) {
    p->spout->ReleaseSender ();
    p->spout->CloseDirectX11 ();
    delete p->spout;
  }
  if (p->gst_dev) gst_object_unref (p->gst_dev);
  if (p->context4) p->context4->Release ();
  if (p->device5) p->device5->Release ();
  if (p->context) p->context->Release ();
  if (p->device) p->device->Release ();
  delete p;
}

TCS_GST_API int
tcs_player_load (TcsPlayer* player, const char* utf8_path, double start_sec,
                 int paused, char* errbuf, size_t errbuf_len)
{
  if (!player || !utf8_path || !utf8_path[0])
    return TCS_ERR_GENERIC;
  /* The first load fixes the decode mode (tcs_player_set_decode_mode). Both
   * this write and the setter run on the control thread only. */
  player->ever_loaded = true;
  /* v0.5.0: HAP かどうかは素材ごとに決まる。読み込みのたびに判定し直す
   * （展開器そのものは使い回す。作り直しはテクスチャの寸法が変わったときだけ）。 */
  player->hap_stream = false;
  int rc = build_pipeline (player, utf8_path, start_sec, paused);
  if (rc != TCS_OK && errbuf && errbuf_len)
    snprintf (errbuf, errbuf_len, "%s", player->last_error.c_str ());
  return rc;
}

TCS_GST_API int
tcs_player_stop (TcsPlayer* player)
{
  if (!player) return TCS_ERR_GENERIC;
  teardown_pipeline (player);
  player->path.clear ();
  player->width = player->height = 0;
  player->fps = 0.0;
  player->duration = -1.0;
  std::lock_guard<std::mutex> g (player->frame_lock);
  player->decoder_name.clear ();
  return TCS_OK;
}

TCS_GST_API int
tcs_player_set_paused (TcsPlayer* player, int paused)
{
  if (!player) return TCS_ERR_GENERIC;
  /* Do not hold frame_lock across the state change: the streaming thread may
   * be inside on_new_sample waiting for frame_lock while holding the sink's
   * stream lock, and the state change needs that stream lock (deadlock). */
  GstElement* pipeline;
  log_pipe_state (player, paused ? "set_paused.before(yes)" : "set_paused.before(no)");
  bool skip_state_change = false;
  {
    /* Serialize the final decision with the pump's PAUSED return so a resume
     * always wins over a pump that has just delivered its frame. */
    std::lock_guard<std::mutex> st (player->state_mutex);
    {
      std::lock_guard<std::mutex> g (player->frame_lock);
      player->paused = paused != 0;
      if (paused) {
        /* A pause request while the pump is delivering stays PLAYING until
         * the pump has the frame; the pump restores PAUSED itself. */
        skip_state_change = player->pump_active;
      } else {
        /* Resume wins: cancel the pump and unmute before going PLAYING. */
        if (player->pump_active) {
          player->pump_active = false;
          player->pump_pending.store (false, std::memory_order_relaxed);
        }
        if (player->pump_muted) {
          player->pump_muted = false;
          apply_volume_locked (player);
        }
      }
      pipeline = player->pipeline;
    }
    if (pipeline && !skip_state_change)
      gst_element_set_state (pipeline,
          paused ? GST_STATE_PAUSED : GST_STATE_PLAYING);
  }
  log_pipe_state (player, paused ? "set_paused.after(yes)" : "set_paused.after(no)");
  return TCS_OK;
}

TCS_GST_API int
tcs_player_get_paused (TcsPlayer* player)
{
  return player ? (player->paused ? 1 : 0) : -1;
}

TCS_GST_API int
tcs_player_set_decode_mode (TcsPlayer* player, int mode)
{
  if (!player)
    return TCS_ERR_GENERIC;
  if (mode != TCS_DECODE_MODE_HARDWARE && mode != TCS_DECODE_MODE_SOFTWARE)
    return TCS_ERR_INVALID_ARG;

  /* Thread-confinement invariant: this setter and build_pipeline both run on
   * the control thread that owns load/seek/pause, so the field needs no
   * cross-thread protocol. state_mutex makes the ordering with the other
   * control-thread entry points explicit; no GStreamer call is made while it
   * is held. */
  std::lock_guard<std::mutex> st (player->state_mutex);
  if (player->ever_loaded)
    return TCS_ERR_GENERIC;
  player->decode_mode = mode;
  return TCS_OK;
}

TCS_GST_API uint64_t
tcs_player_seek (TcsPlayer* player, double seconds)
{
  if (!player) return 0;
  log_pipe_state (player, "seek.before");
  uint64_t gen;
  bool was_paused;
  SeekRequest req;
  {
    std::lock_guard<std::mutex> g (player->frame_lock);
    was_paused = player->paused;
    gen = seek_prepare_locked (player, seconds, player->rate, &req);
  }
  /* The flushing seek goes out with no lock held (see SeekRequest). */
  bool sent = seek_send (player, req);
  log_pipe_state (player, "seek.after");
  /* EOS restart deferred out of seek_prepare_locked (frame_lock was held there). */
  apply_pending_play_restart (player);
  /* 0.4.6: a rejected seek is reported as a failure (0). It used to return the
   * new generation, so the owner treated it as issued and waited for a landing
   * that could never come, and the pump below was armed for a flush that never
   * happened. Seen only while a load was still settling (7 times in the
   * 2026-09-18 test logs, never while following). seek_send already stopped
   * the pre-seek filtering, so frames keep flowing; the owner decides whether
   * to retry. */
  if (!sent)
    return 0;
  /* A paused pipeline cannot render the post-flush preroll (appsink sync=true
   * with a stopped clock). Arm the non-blocking pump: <=0.1ms here, delivery
   * and the PAUSED return happen on the bus thread. */
  if (was_paused)
    pump_arm (player, gen);
  return gen;
}

TCS_GST_API uint64_t
tcs_player_step_frame (TcsPlayer* player)
{
  if (!player) return 0;
  bool wasPaused;
  uint64_t gen;
  SeekRequest req;
  log_pipe_state (player, "step.before");
  {
    std::lock_guard<std::mutex> g (player->frame_lock);
    if (!player->pipeline) return player->generation;
    gint64 pos = 0;
    gst_element_query_position (player->pipeline, GST_FORMAT_TIME, &pos);
    double step = player->fps > 0.0 ? 1.0 / player->fps : 0.04;
    wasPaused = player->paused;
    /* the step target goes through the same TS gate as a manual seek */
    gen = seek_prepare_locked (player, (double) pos / GST_SECOND + step, player->rate, &req);
  }
  seek_send (player, req);
  apply_pending_play_restart (player);
  /* D24: a step on a paused pipeline is a paused seek with the same problem:
   * PAUSED sinks do not re-preroll after a flush seek, and the fixed 500ms
   * PLAYING window was too short for a long GOP (the frame was lost again on
   * PAUSED). Use the shared pump: non-blocking, budget-bounded (4s default),
   * and it restores PAUSED as soon as the stepped frame is queued. */
  if (wasPaused)
    pump_arm (player, gen);
  return gen;
}

TCS_GST_API uint64_t
tcs_player_set_generation (TcsPlayer* player, uint64_t generation)
{
  if (!player) return 0;
  std::lock_guard<std::mutex> g (player->frame_lock);
  player->generation = generation;
  return player->generation;
}

TCS_GST_API uint64_t
tcs_player_get_generation (TcsPlayer* player)
{
  if (!player) return 0;
  std::lock_guard<std::mutex> g (player->frame_lock);
  return player->generation;
}

TCS_GST_API int
tcs_player_set_speed (TcsPlayer* player, double rate)
{
  if (!player) return TCS_ERR_GENERIC;
  GstElement* pipeline = nullptr;
  double new_rate = 1.0;
  gint64 pos = 0;
  {
    std::lock_guard<std::mutex> g (player->frame_lock);
    player->rate = rate > 0.0 ? rate : 1.0;
    if (!player->pipeline) return TCS_OK;
    /* a rate change re-seeks; the TS segment rewrite does not apply to it */
    player->rebase_armed = false;
    player->rebase_seek_seqnum = 0;
    new_rate = player->rate;
    gst_element_query_position (player->pipeline, GST_FORMAT_TIME, &pos);
    pipeline = player->pipeline;
  }
  /* The flushing seek goes out with no lock held (the rule in SeekRequest). */
  LOG ("seek: send begin (rate) target_ns=%lld", (long long) pos);
  gboolean ok = gst_element_seek (pipeline, new_rate, GST_FORMAT_TIME,
      (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE | GST_SEEK_FLAG_SKIP),
      GST_SEEK_TYPE_SET, pos, GST_SEEK_TYPE_NONE, -1);
  LOG ("seek: send end (rate) ok=%d", ok ? 1 : 0);
  return TCS_OK;
}

TCS_GST_API int
tcs_player_set_rate_instant (TcsPlayer* player, double rate)
{
  if (!player) return TCS_ERR_GENERIC;
  if (!std::isfinite (rate) || rate <= 0.0) return TCS_ERR_GENERIC;
#if GST_CHECK_VERSION(1,18,0)
  GstElement* pipeline = nullptr;
  {
    /* I13: the locks guard the decision only (paused / rate / pipeline); the
     * GStreamer call goes out with no lock held. Lock order matches
     * tcs_player_set_paused (state_mutex -> frame_lock). */
    std::lock_guard<std::mutex> st (player->state_mutex);
    std::lock_guard<std::mutex> g (player->frame_lock);
    /* A non-flushing seek in PAUSED is undefined; refuse instead of guessing. */
    if (player->paused) return TCS_ERR_GENERIC;
    /* 0.4.6: player->rate is written only after GStreamer accepted the change
     * (below). It used to be written here, before the call: a rejected change
     * left the new rate stored, and the next ordinary seek (seek_prepare_locked
     * uses player->rate) applied it, so the app's idea of the rate and the
     * pipeline's diverged. */
    /* The instant rate change rewrites the segment like a rate seek: the TS
     * rebase rewrite does not apply to it (same reasoning as set_speed). */
    player->rebase_armed = false;
    player->rebase_seek_seqnum = 0;
    pipeline = player->pipeline;
  }
  if (!pipeline) return TCS_ERR_NOT_LOADED;
  LOG ("seek: send begin (rate.instant) rate=%.6f", rate);
  gboolean ok = gst_element_seek (pipeline, rate, GST_FORMAT_TIME,
      GST_SEEK_FLAG_INSTANT_RATE_CHANGE,
      GST_SEEK_TYPE_NONE, GST_CLOCK_TIME_NONE,
      GST_SEEK_TYPE_NONE, GST_CLOCK_TIME_NONE);
  LOG ("seek: send end (rate.instant) ok=%d", ok ? 1 : 0);
  if (!ok)
    return TCS_ERR_GENERIC;
  {
    /* Same lock order as above (state_mutex -> frame_lock); no GStreamer call
     * under either (I13). */
    std::lock_guard<std::mutex> st (player->state_mutex);
    std::lock_guard<std::mutex> g (player->frame_lock);
    player->rate = rate;
  }
  return TCS_OK;
#else
  /* GST_SEEK_FLAG_INSTANT_RATE_CHANGE needs GStreamer 1.18+. */
  return TCS_ERR_GENERIC;
#endif
}

TCS_GST_API int
tcs_player_set_volume (TcsPlayer* player, double volume_0_to_100)
{
  if (!player) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  player->volume_value = volume_0_to_100;
  apply_volume_locked (player);
  return TCS_OK;
}

TCS_GST_API int
tcs_player_set_mute (TcsPlayer* player, int mute)
{
  if (!player) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  player->muted = mute != 0;
  apply_volume_locked (player);
  return TCS_OK;
}

/* Shared body for tcs_player_get_time_pos / _ex. Caller holds frame_lock.
 * The legacy function passes out == nullptr and sees exactly the old values;
 * _ex additionally gets the basis, generations and the delivered PTS. */
static int
get_time_pos_locked (TcsPlayer* player, double* out_sec, TcsPositionSample* out)
{
  if (!player || !out_sec) return TCS_ERR_GENERIC;
  if (!player->pipeline || player->path.empty ()) return TCS_ERR_NOT_LOADED;
  if (out) {
    out->seconds = 0.0;
    out->basis = TCS_POSITION_BASIS_NONE;
    out->generation = 0;
    out->current_generation = player->generation;
  }
  /* D25-c: while paused (and the paused-seek pump has finished) report the
   * newest delivered video frame's stream-time PTS. The pipeline position is
   * dominated by the audio sink, which keeps advancing during the muted pump
   * (target + pump duration); the owner's freeze/landing gates compare this
   * value against the frame target with a +/-2 frame tolerance, so a correctly
   * landed frame looked out of range and the freeze timed out. While playing,
   * keep the pipeline position. latest_pts_ns is already the D10 stream-time
   * mapped PTS (the same space the seek target uses). */
  bool paused_frame_pos = player->paused && !player->pump_active &&
      player->latest_pts_ns > 0 && player->latest_gen == player->generation;
  gint64 pos = 0;
  int32_t basis = TCS_POSITION_BASIS_PIPELINE;
  uint64_t generation = player->generation;
  if (paused_frame_pos) {
    pos = (gint64) player->latest_pts_ns;
    basis = TCS_POSITION_BASIS_DELIVERED;
    generation = player->latest_gen;
  } else if (!gst_element_query_position (player->pipeline, GST_FORMAT_TIME, &pos) || pos < 0) {
    /* D10: the pipeline query reports stream time (so it already maps the
     * qtdemux post-seek shift back). When the query is unavailable, fall back
     * to the newest delivered frame's stream-mapped PTS - never the raw PTS.
     * 0.4.5-A phase 2: the fallback value must belong to the CURRENT
     * generation. The query fails 0.3-0.6 ms after a seek is issued, while
     * latest_gen still points at the pre-seek pipeline; returning that PTS
     * would hand the owner a position from before the seek. Without a
     * current-generation value the position is unknown, so report
     * TCS_ERR_NOT_LOADED: every caller already treats that as "no position"
     * (the sync coordinator defers, the UI uses null, the freeze capture
     * waits). A zero-valued basis NONE return is not an option here: the
     * legacy getter has no basis field and 0.0 is a valid media position. */
    if (player->latest_pts_ns > 0) {
      int allowed = tcs_position_fallback_allowed (player->latest_gen, player->generation);
      if (player->position_trace) {
        LARGE_INTEGER now;
        QueryPerformanceCounter (&now);
        delivery_append_locked (player, (uint64_t) now.QuadPart, player->latest_seq,
            (int64_t) player->latest_pts_ns,
            allowed ? (int64_t) player->latest_pts_ns : 0,
            (uint32_t) (kDeliveryFlagPosition | kDeliveryFlagPositionFallback |
                (allowed ? 0 : kDeliveryFlagPositionFallbackRejected)));
      }
      if (!allowed)
        return TCS_ERR_NOT_LOADED;
      *out_sec = (double) player->latest_pts_ns / GST_SECOND;
      if (out) {
        out->seconds = *out_sec;
        out->basis = TCS_POSITION_BASIS_DELIVERED;
        out->generation = player->latest_gen;
      }
      return TCS_OK;
    }
    return TCS_ERR_NOT_LOADED;
  }
  *out_sec = (double) pos / GST_SECOND;
  if (out) {
    out->seconds = *out_sec;
    out->basis = basis;
    out->generation = generation;
    out->delivered_seconds = (double) player->latest_pts_ns / GST_SECOND;
    out->delivered_generation = player->latest_gen;
  }
  if (player->position_trace) {
    /* One snapshot per query. frame_lock freezes latest_seq/latest_pts_ns
     * while the QPC is taken, so the owner gets the queried position and the
     * newest delivery PTS from the same instant in one trace line:
     * running_ns = queried position, pts_ns/seq = newest delivered frame. */
    LARGE_INTEGER now;
    QueryPerformanceCounter (&now);
    delivery_append_locked (player, (uint64_t) now.QuadPart, player->latest_seq,
        (int64_t) player->latest_pts_ns, (int64_t) pos, (uint32_t) kDeliveryFlagPosition);
  }
  return TCS_OK;
}

TCS_GST_API int
tcs_player_get_time_pos (TcsPlayer* player, double* out_sec)
{
  if (!player || !out_sec) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  return get_time_pos_locked (player, out_sec, nullptr);
}

TCS_GST_API int
tcs_player_get_time_pos_ex (TcsPlayer* player, TcsPositionSample* out)
{
  if (!player || !out) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  double seconds = 0.0;
  int rc = get_time_pos_locked (player, &seconds, out);
  if (rc == TCS_OK)
    out->seconds = seconds;
  return rc;
}

TCS_GST_API int
tcs_player_get_duration (TcsPlayer* player, double* out_sec)
{
  if (!player || !out_sec) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (player->duration > 0.0) {
    *out_sec = player->duration;
    return TCS_OK;
  }
  if (!player->pipeline) return TCS_ERR_NOT_LOADED;
  gint64 dur = 0;
  if (gst_element_query_duration (player->pipeline, GST_FORMAT_TIME, &dur) && dur > 0) {
    player->duration = (double) dur / GST_SECOND;
    *out_sec = player->duration;
    return TCS_OK;
  }
  return TCS_ERR_NOT_LOADED;
}

TCS_GST_API int
tcs_player_get_fps (TcsPlayer* player, double* out_fps)
{
  if (!player || !out_fps) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (player->fps <= 0.0) return TCS_ERR_NOT_LOADED;
  *out_fps = player->fps;
  return TCS_OK;
}

TCS_GST_API int
tcs_player_get_path (TcsPlayer* player, char* out, size_t out_len)
{
  if (!player || !out || !out_len) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  snprintf (out, out_len, "%s", player->path.c_str ());
  return TCS_OK;
}

TCS_GST_API int
tcs_player_get_size (TcsPlayer* player, int* out_w, int* out_h)
{
  if (!player) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (player->width <= 0 || player->height <= 0) return TCS_ERR_NOT_LOADED;
  if (out_w) *out_w = player->width;
  if (out_h) *out_h = player->height;
  return TCS_OK;
}

TCS_GST_API void
tcs_player_set_frame_callback (TcsPlayer* player, tcs_frame_notify_fn fn, void* user_data)
{
  if (!player) return;
  std::lock_guard<std::mutex> g (player->frame_lock);
  player->notify = fn;
  player->notify_user = user_data;
}

TCS_GST_API int
tcs_player_consume_update (TcsPlayer* player)
{
  if (!player) return 0;
  std::lock_guard<std::mutex> g (player->frame_lock);
  bool v = player->pending_update;
  player->pending_update = false;
  return v ? 1 : 0;
}

/* ---- LEASE API ---- */

TCS_GST_API int
tcs_player_acquire (TcsPlayer* player, uint64_t generation, TcsFrameInfo* out_info)
{
  if (!player || !out_info) return 0;
  std::unique_lock<std::mutex> g (player->frame_lock);
  TcsPlayer* p = player;

  /* TS seek stall diagnostics: if the gate has been waiting for seconds,
   * report the pipeline/sink states and the audio-side buffer flow once. */
  if (p->gate_active && !p->gate_diag_done && p->gate_armed_qpc && p->pipeline) {
    LARGE_INTEGER now2;
    QueryPerformanceCounter (&now2);
    double ms = p->qpc_freq > 0
        ? (double) (now2.QuadPart - (long long) p->gate_armed_qpc) * 1000.0 /
            (double) p->qpc_freq
        : 0.0;
    if (ms > 3000.0) {
      p->gate_diag_done = true;
      GstState c = GST_STATE_VOID_PENDING, pend = GST_STATE_VOID_PENDING;
      gst_element_get_state (p->pipeline, &c, &pend, 0);
      GstState av = GST_STATE_VOID_PENDING, ap = GST_STATE_VOID_PENDING;
      if (p->appsink) gst_element_get_state (p->appsink, &av, &ap, 0);
      GstElement* as = find_audio_sink_element (p);
      GstState sv = GST_STATE_VOID_PENDING, sp = GST_STATE_VOID_PENDING;
      if (as) gst_element_get_state (as, &sv, &sp, 0);
      LOG ("seek: diag stalled %.0fms pipe=%d/%d vsink=%d/%d asink=%s=%d/%d "
          "au_bufs=%llu au_first=%lld au_last=%lld "
          "vp_bufs=%llu vp_first=%lld vp_last=%lld vframes=%llu dropped=%llu",
          ms, (int) c, (int) pend, (int) av, (int) ap,
          as ? GST_ELEMENT_NAME (as) : "?", (int) sv, (int) sp,
          (unsigned long long) p->au_probe_buffers.load (),
          (long long) p->au_probe_first_pts.load (),
          (long long) p->au_probe_last_pts.load (),
          (unsigned long long) p->vp_probe_buffers.load (),
          (long long) p->vp_probe_first_pts.load (),
          (long long) p->vp_probe_last_pts.load (),
          (unsigned long long) p->frames_decoded,
          (unsigned long long) p->gate_dropped);
    }
  }

  /* Already leased frame of this generation and not stale: keep it. */
  if ((p->leased || p->leased_slot >= 0) && p->lease_info.generation == generation) {
    *out_info = p->lease_info;
    return 1;
  }
  /* Drop a lease from an older generation? No: the compositor owns it until
   * it releases. If a lease is still held, refuse silently (none). */
  if (p->leased || p->leased_slot >= 0) {
    int32_t held_slot = p->leased_slot;
    uint64_t held_gen = p->lease_info.generation;
    g.unlock ();
    if (lease_log)
      LOG ("lease: acquire refused (lease held) want_gen=%llu held_gen=%llu slot=%d",
          (unsigned long long) generation, (unsigned long long) held_gen, held_slot);
    return 0;
  }

  /* Deliver with bounded latency (problem H-2/H-3): drop older/other
   * generations, then apply the pure delivery policy to the backlog. */
  while (!p->frames.empty() && p->frames.front().generation != generation) {
    gst_sample_unref (p->frames.front().sample);
    p->frames.pop_front();
  }
  if (p->frames.empty()) {
    int rc = p->eos ? TCS_ERR_ENDED : 0;
    g.unlock ();
    if (lease_log)
      LOG ("lease: acquire none (no frame) gen=%llu rc=%d",
          (unsigned long long) generation, rc);
    return rc;
  }

  /* H-3: an n==2 backlog older than 1.25 frames is steady clock drift and
   * loses its oldest frame instead of waiting for a fixed streak. */
  LARGE_INTEGER now_qpc;
  QueryPerformanceCounter (&now_qpc);
  uint64_t oldest_age = (uint64_t) now_qpc.QuadPart > p->frames.front().arrival_qpc
      ? (uint64_t) now_qpc.QuadPart - p->frames.front().arrival_qpc : 0;
  uint64_t age_limit = p->qpc_freq > 0
      ? (uint64_t) ((p->qpc_freq * (int64_t) TCS_DELIVERY_AGE_LIMIT_US) / 1000000) : 0;
  TcsDeliveryPlan plan = tcs_delivery_plan ((uint32_t) p->frames.size (), oldest_age, age_limit);
  for (uint32_t i = 0; i < plan.drop_oldest && !p->frames.empty(); i++) {
    gst_sample_unref (p->frames.front().sample);
    p->frames.pop_front();
    p->delivery_replaced++;
  }
  if (!plan.lease || p->frames.empty()) {
    int rc = p->eos ? TCS_ERR_ENDED : 0;
    uint32_t backlog = (uint32_t) p->frames.size ();
    g.unlock ();
    if (lease_log)
      LOG ("lease: acquire none (policy) gen=%llu rc=%d backlog=%u drop_oldest=%u",
          (unsigned long long) generation, rc, backlog, plan.drop_oldest);
    return rc;
  }

  TcsPlayer::FrameSlot slot = p->frames.front();
  p->frames.pop_front();
  p->leased = slot.sample;   /* take ownership; pool texture now pinned (legacy) */
  p->leased_slot = slot.slot;
  memset (&p->lease_info, 0, sizeof (p->lease_info));
  p->lease_info.generation = slot.generation;
  p->lease_info.seq = slot.seq;
  p->lease_info.pts_ns = (int64_t) slot.pts_ns;
  p->lease_info.width = p->width;
  p->lease_info.height = p->height;
  p->lease_info.is_gpu = slot.gpu ? 1 : 0;
  p->lease_info.slot = slot.slot;
  p->lease_info.ring_epoch = slot.ring_epoch;
  *out_info = p->lease_info;
  {
    uint64_t leased_gen = p->lease_info.generation;
    uint64_t leased_seq = p->lease_info.seq;
    int32_t leased_slot = p->lease_info.slot;
    int is_gpu = p->lease_info.is_gpu;
    int w = p->lease_info.width, h = p->lease_info.height;
    uint32_t backlog = (uint32_t) p->frames.size ();
    g.unlock ();
    if (lease_log)
      LOG ("lease: acquire gen=%llu seq=%llu slot=%d gpu=%d %dx%d backlog=%u",
          (unsigned long long) leased_gen, (unsigned long long) leased_seq,
          leased_slot, is_gpu, w, h, backlog);
  }
  return 1;
}

TCS_GST_API int
tcs_player_leased_texture (TcsPlayer* player, void** out_texture,
                           uint32_t* out_subresource, uint32_t* out_dxgi_format)
{
  if (!player || !out_texture) return TCS_ERR_NO_FRAME;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (!player->leased && player->leased_slot < 0) return TCS_ERR_NO_FRAME;
  guint sub = 0;
  ID3D11Texture2D* tex = texture_of_lease (player, &sub);
  if (!tex) return TCS_ERR_NO_FRAME;
  D3D11_TEXTURE2D_DESC desc;
  tex->GetDesc (&desc);
  *out_texture = tex;              /* borrowed; valid until release() */
  if (out_subresource) *out_subresource = sub;
  if (out_dxgi_format) *out_dxgi_format = (uint32_t) desc.Format;
  return TCS_OK;
}

TCS_GST_API void
tcs_player_release (TcsPlayer* player)
{
  if (!player) return;
  int32_t released_slot;
  uint64_t released_seq, released_gen;
  bool had_sample;
  {
    std::lock_guard<std::mutex> g (player->frame_lock);
    had_sample = player->leased != nullptr;
    released_slot = player->leased_slot;
    released_seq = player->lease_info.seq;
    released_gen = player->lease_info.generation;
    if (player->leased) {
      gst_sample_unref (player->leased);   /* returns the pool texture */
      player->leased = nullptr;
    }
    /* free the ring slot last: the compositor has finished with it. */
    player->leased_slot = -1;
  }
  if (lease_log)
    LOG ("lease: release gen=%llu seq=%llu slot=%d sample=%d",
        (unsigned long long) released_gen, (unsigned long long) released_seq,
        released_slot, had_sample ? 1 : 0);
}

/* Stage 6b: NT handles + shared fence of the ring (shim-owned handles). */
TCS_GST_API int
tcs_player_ring_info (TcsPlayer* player, void** out_handles, uint32_t capacity,
                      uint32_t* out_count, void** out_fence, uint32_t* out_width,
                      uint32_t* out_height)
{
  if (!player || !out_handles || !out_count) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (!player->ring_ready) return TCS_ERR_NO_FRAME;
  if (capacity < TcsPlayer::kRingSlots) return TCS_ERR_SIZE;
  for (uint32_t i = 0; i < TcsPlayer::kRingSlots; i++)
    out_handles[i] = player->ring_handle[i];
  *out_count = TcsPlayer::kRingSlots;
  if (out_fence) *out_fence = player->ring_fence_handle;
  if (out_width) *out_width = (uint32_t) player->ring_width;
  if (out_height) *out_height = (uint32_t) player->ring_height;
  return TCS_OK;
}

/* D8: current ring epoch. The compositor reopens the ring when the epoch
 * stamped in TcsFrameInfo differs from this value. */
TCS_GST_API int
tcs_player_ring_epoch (TcsPlayer* player, uint32_t* out_epoch)
{
  if (!player || !out_epoch) return TCS_ERR_INVALID_ARG;
  std::lock_guard<std::mutex> g (player->frame_lock);
  *out_epoch = player->ring_epoch;
  return TCS_OK;
}

/* ---- verification layer ---- */

TCS_GST_API int
tcs_player_publish_spout (TcsPlayer* player)
{
  if (!player) return TCS_ERR_GENERIC;
  if (!player->spout_ready) return TCS_ERR_SPOUT;
  std::lock_guard<std::mutex> g (player->frame_lock);
  bool ok = false;

  if (player->leased_slot >= 0) {
    /* D8: a ring lease from an older epoch cannot be published through the
     * rebuilt ring (verification layer only). */
    if (player->lease_info.ring_epoch != player->ring_epoch)
      return TCS_ERR_NO_FRAME;
    /* ring lease: send the shared ring texture on the shim device */
    ID3D11Texture2D* tex = player->ring_texture[player->leased_slot];
    if (tex)
      ok = player->spout->SendTexture (tex);
    if (ok) player->spout_sends++;
    return ok ? TCS_OK : TCS_ERR_GENERIC;
  }

  GstSample* sample = player->leased ? player->leased
      : (player->frames.empty() ? nullptr : player->frames.back().sample);
  if (!sample) return TCS_ERR_NO_FRAME;
  GstBuffer* buf = gst_sample_get_buffer (sample);
  GstMemory* mem = buf ? gst_buffer_peek_memory (buf, 0) : nullptr;

  if (mem && gst_is_d3d11_memory (mem)) {
    guint sub = 0;
    GstSample* keep = player->leased;
    player->leased = sample; /* texture_of_lease reads the lease slot */
    ID3D11Texture2D* tex = texture_of_lease (player, &sub);
    player->leased = keep;
    if (tex)
      ok = player->spout->SendTexture (tex);
  } else if (buf) {
    GstMapInfo info;
    if (gst_buffer_map (buf, &info, GST_MAP_READ)) {
      gsize stride = (gsize) player->lease_info.width * 4;
      GstVideoMeta* meta = gst_buffer_get_video_meta (buf);
      if (meta) stride = meta->stride[0];
      ok = player->spout->SendImage (info.data, player->width, player->height, (unsigned) stride);
      gst_buffer_unmap (buf, &info);
    }
  }
  if (ok) player->spout_sends++;
  return ok ? TCS_OK : TCS_ERR_GENERIC;
}

TCS_GST_API int
tcs_player_send_image (TcsPlayer* player, const uint8_t* bgra, int width, int height, int pitch)
{
  if (!player || !bgra || width <= 0 || height <= 0) return TCS_ERR_GENERIC;
  if (!player->spout_ready) return TCS_ERR_SPOUT;
  bool ok = player->spout->SendImage (bgra, width, height, pitch);
  if (ok) {
    std::lock_guard<std::mutex> g (player->frame_lock);
    player->spout_sends++;
  }
  return ok ? TCS_OK : TCS_ERR_GENERIC;
}

TCS_GST_API int
tcs_player_get_stats (TcsPlayer* player, TcsStats* out)
{
  if (!player || !out) return TCS_ERR_GENERIC;
  memset (out, 0, sizeof (*out));
  std::lock_guard<std::mutex> g (player->frame_lock);
  snprintf (out->decoder, sizeof (out->decoder), "%s", player->decoder_name.c_str ());
  snprintf (out->last_error, sizeof (out->last_error), "%s", player->last_error.c_str ());
  out->gpu_path = player->latest_gpu || (player->leased && player->lease_info.is_gpu) ? 1 : 0;
  out->eos = player->eos ? 1 : 0;
  out->error = player->failed ? 1 : 0;
  out->width = player->width;
  out->height = player->height;
  out->frames_decoded = player->frames_decoded;
  out->spout_sends = player->spout_sends;
  out->generation = player->generation;
  return TCS_OK;
}

TCS_GST_API int
tcs_player_get_gop_status (TcsPlayer* player, TcsGopStatus* out)
{
  if (!player || !out) return TCS_ERR_GENERIC;
  memset (out, 0, sizeof (*out));
  std::lock_guard<std::mutex> g (player->gop_lock);
  const TcsGopTracker& t = player->gop_tracker;
  out->state = t.state;
  out->active = player->gop_active ? 1 : 0;
  out->keyframes = t.keyframes;
  out->median_interval_sec = (double) t.median_interval_ns / 1e9;
  out->pending_sec = (double) t.pending_ns / 1e9;
  out->threshold_sec = (double) t.threshold_ns / 1e9;
  out->warning_qpc = t.warning_qpc;
  return TCS_OK;
}

TCS_GST_API int
tcs_player_drain_delivery_events (TcsPlayer* player, TcsDeliveryEvent* out,
                                  uint32_t capacity, uint32_t* out_count)
{
  if (!player || !out || !out_count) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  uint32_t n = 0;
  while (n < capacity && player->delivery_read != player->delivery_write) {
    out[n++] = player->delivery_ring[player->delivery_read % TcsPlayer::kDeliveryCapacity];
    player->delivery_read++;
  }
  *out_count = n;
  return TCS_OK;
}

TCS_GST_API int
tcs_player_get_delivery_stats (TcsPlayer* player, TcsDeliveryStats* out)
{
  if (!player || !out) return TCS_ERR_GENERIC;
  memset (out, 0, sizeof (*out));
  std::lock_guard<std::mutex> g (player->frame_lock);
  out->arrivals = player->delivery_arrivals;
  out->latest_replaced = player->delivery_replaced;
  out->qos_events = player->delivery_qos.load (std::memory_order_relaxed);
  out->decoder_out = player->delivery_decoder_out.load (std::memory_order_relaxed);
  out->ring_dropped = player->delivery_ring_dropped;
  out->last_qpc = player->delivery_last_qpc;
  return TCS_OK;
}

TCS_GST_API int
tcs_player_decoder_name (TcsPlayer* player, char* out, size_t out_len)
{
  if (!player || !out || !out_len) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  snprintf (out, out_len, "%s", player->decoder_name.c_str ());
  return TCS_OK;
}

TCS_GST_API int
tcs_player_spout_ready (TcsPlayer* player)
{
  return player && player->spout_ready ? 1 : 0;
}


/* ---- 0.4.5-C3: static keyframe scan ------------------------------------
 * filesrc ! parsebin ! fakesink with a pad probe on every parsebin src pad.
 * Nothing is decoded. Own pipeline, no TcsPlayer state, no frame_lock. */

struct GopScanCtx {
  std::mutex            lock;
  std::vector<double>   key_times;   /* seconds */
  GstElement*           pipeline = nullptr;
  /* v0.5.0: HAP は全フレームがキーフレーム。ファイルを読み切らずに打ち切る。 */
  bool                  intra_only = false;
  double                intra_fps = 0.0;
};

static GstPadProbeReturn
gop_scan_probe (GstPad* /*pad*/, GstPadProbeInfo* info, gpointer user_data)
{
  GstBuffer* buf = GST_PAD_PROBE_INFO_BUFFER (info);
  if (!buf)
    return GST_PAD_PROBE_OK;
  /* A keyframe is a buffer WITHOUT the delta-unit flag. Read it on the parsed
   * (still encoded) stream: after decoding the flag is not meaningful. */
  if (GST_BUFFER_FLAG_IS_SET (buf, GST_BUFFER_FLAG_DELTA_UNIT))
    return GST_PAD_PROBE_OK;
  GstClockTime pts = GST_BUFFER_PTS (buf);
  if (!GST_CLOCK_TIME_IS_VALID (pts))
    return GST_PAD_PROBE_OK;
  GopScanCtx* ctx = static_cast<GopScanCtx*> (user_data);
  std::lock_guard<std::mutex> g (ctx->lock);
  ctx->key_times.push_back ((double) pts / (double) GST_SECOND);
  return GST_PAD_PROBE_OK;
}

/* parsebin exposes one pad per elementary stream. Every pad needs a sink or the
 * unlinked branch stops the whole pipeline with not-linked, so each pad gets its
 * own fakesink; only the video pad carries the keyframe probe. */
static void
gop_scan_pad_added (GstElement* /*parsebin*/, GstPad* pad, gpointer user_data)
{
  GopScanCtx* ctx = static_cast<GopScanCtx*> (user_data);
  /* At pad-added the pad may not carry current caps yet; fall back to the
   * negotiable set, which parsebin has already narrowed to the real media. */
  GstCaps* caps = gst_pad_get_current_caps (pad);
  if (!caps)
    caps = gst_pad_query_caps (pad, nullptr);
  bool is_video = false;
  bool is_hap = false;
  int fps_n = 0, fps_d = 0;
  if (caps) {
    const GstStructure* st = gst_caps_get_structure (caps, 0);
    const char* name = st ? gst_structure_get_name (st) : nullptr;
    is_video = name && g_str_has_prefix (name, "video/");
    is_hap = name && g_str_equal (name, "video/x-hap");
    if (st)
      gst_structure_get_fraction (st, "framerate", &fps_n, &fps_d);
    gst_caps_unref (caps);
  }
  if (is_hap) {
    /* v0.5.0: HAP は全フレームがキーフレーム（フレーム間の予測が無い）。答えは読む前に決まるので、
     * 再生と同じディスクを数 GB 読み切らずに打ち切る（検証機で 10GB の HAP に 6.5 秒、
     * 再生の開始直後と重なっていた）。 */
    {
      std::lock_guard<std::mutex> g (ctx->lock);
      ctx->intra_only = true;
      ctx->intra_fps = (fps_n > 0 && fps_d > 0) ? (double) fps_n / (double) fps_d : 0.0;
    }
    LOG ("gop-scan: video/x-hap is intra-only (every frame is a keyframe); stopping the read");
    gst_element_post_message (ctx->pipeline, gst_message_new_eos (GST_OBJECT (ctx->pipeline)));
  } else if (is_video) {
    gst_pad_add_probe (pad, GST_PAD_PROBE_TYPE_BUFFER, gop_scan_probe, ctx, nullptr);
  }

  GstElement* sink = gst_element_factory_make ("fakesink", nullptr);
  if (!sink)
    return;
  g_object_set (sink, "sync", FALSE, "async", FALSE, nullptr);
  gst_bin_add (GST_BIN (ctx->pipeline), sink);
  gst_element_sync_state_with_parent (sink);
  GstPad* sinkpad = gst_element_get_static_pad (sink, "sink");
  if (sinkpad) {
    gst_pad_link (pad, sinkpad);
    gst_object_unref (sinkpad);
  }
}

static double
gop_percentile (const std::vector<double>& sorted, double frac)
{
  if (sorted.empty ()) return 0.0;
  if (sorted.size () == 1) return sorted[0];
  double pos = frac * (double) (sorted.size () - 1);
  size_t lo = (size_t) pos;
  size_t hi = lo + 1 < sorted.size () ? lo + 1 : lo;
  double t = pos - (double) lo;
  return sorted[lo] + t * (sorted[hi] - sorted[lo]);
}

TCS_GST_API int
tcs_scan_gop (const char* utf8_path, int32_t budget_ms, TcsGopScan* out)
{
  if (!utf8_path || !out)
    return TCS_ERR_INVALID_ARG;
  memset (out, 0, sizeof (*out));
  std::call_once (g_gst_once, [] {
    int argc = 0;
    gst_init (&argc, nullptr);
  });

  GstElement* pipeline = gst_pipeline_new ("tcs-gop-scan");
  GstElement* src = gst_element_factory_make ("filesrc", nullptr);
  GstElement* parse = gst_element_factory_make ("parsebin", nullptr);
  if (!pipeline || !src || !parse) {
    if (src) gst_object_unref (src);
    if (parse) gst_object_unref (parse);
    if (pipeline) gst_object_unref (pipeline);
    return TCS_ERR_GENERIC;
  }

  GopScanCtx ctx;
  ctx.pipeline = pipeline;
  g_object_set (src, "location", utf8_path, nullptr);
  gst_bin_add_many (GST_BIN (pipeline), src, parse, nullptr);
  gst_element_link (src, parse);
  g_signal_connect (parse, "pad-added", G_CALLBACK (gop_scan_pad_added), &ctx);

  int rc = TCS_OK;
  if (gst_element_set_state (pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) {
    LOG ("gop-scan: set_state PLAYING failed");
    rc = TCS_ERR_GENERIC;
  } else {
    /* 既定は 10 分。全部読むのが正しい答え（長いギャップはどこにでもありうる）ので、
     * この上限は「異常に遅い経路への保険」であって通常経路ではない。 */
    GstClockTime budget = (budget_ms > 0 ? (GstClockTime) budget_ms : 600000)
        * GST_MSECOND;
    GstBus* bus = gst_element_get_bus (pipeline);
    GstMessage* msg = gst_bus_timed_pop_filtered (bus, budget,
        (GstMessageType) (GST_MESSAGE_EOS | GST_MESSAGE_ERROR));
    if (!msg) {
      /* 打ち切り。ここまでに見つけたギャップは下限として使える（見つけた長いギャップは
       * 本物だが、「長いギャップが無い」は読んだ範囲までしか言えない）。 */
      out->truncated = 1;
      LOG ("gop-scan: budget reached, returning partial result");
    } else if (GST_MESSAGE_TYPE (msg) == GST_MESSAGE_ERROR) {
      GError* err = nullptr;
      gchar* dbg = nullptr;
      gst_message_parse_error (msg, &err, &dbg);
      LOG ("gop-scan: error %s (%s)", err ? err->message : "?", dbg ? dbg : "");
      if (err) g_error_free (err);
      if (dbg) g_free (dbg);
      rc = TCS_ERR_GENERIC;
    }
    if (msg)
      gst_message_unref (msg);
    gint64 dur = 0;
    if (gst_element_query_duration (pipeline, GST_FORMAT_TIME, &dur) && dur > 0)
      out->duration_sec = (double) dur / (double) GST_SECOND;
    gst_object_unref (bus);
  }
  gst_element_set_state (pipeline, GST_STATE_NULL);

  std::vector<double> keys;
  bool intra_only = false;
  double intra_fps = 0.0;
  {
    std::lock_guard<std::mutex> g (ctx.lock);
    keys = ctx.key_times;
    intra_only = ctx.intra_only;
    intra_fps = ctx.intra_fps;
  }
  gst_object_unref (pipeline);

  if (rc != TCS_OK)
    return rc;
  if (intra_only) {
    /* 全フレームがキーフレーム: 間隔はどこでも 1 フレーム。読んでいないので打ち切り扱いにはしない。 */
    double frame = intra_fps > 0.0 ? 1.0 / intra_fps : 0.0;
    out->truncated = 0;
    out->keyframes = (intra_fps > 0.0 && out->duration_sec > 0.0)
        ? (int32_t) (out->duration_sec * intra_fps + 0.5) : 2;
    if (out->keyframes < 2) out->keyframes = 2;
    out->head_gap_sec = 0.0;
    out->tail_gap_sec = frame;
    out->median_gap_sec = frame;
    out->p95_gap_sec = frame;
    out->max_gap_sec = frame;
    return TCS_OK;
  }
  if (keys.empty ())
    return TCS_OK;   /* keyframes = 0 tells the caller the scan found nothing */

  std::sort (keys.begin (), keys.end ());
  if (out->duration_sec <= 0.0)
    out->duration_sec = keys.back ();

  /* head gap, each inter-keyframe gap, tail gap */
  std::vector<double> gaps;
  gaps.reserve (keys.size () + 1);
  gaps.push_back (keys.front ());
  for (size_t i = 1; i < keys.size (); i++)
    gaps.push_back (keys[i] - keys[i - 1]);
  double tail = out->duration_sec - keys.back ();
  if (tail < 0.0) tail = 0.0;
  gaps.push_back (tail);

  out->keyframes    = (int32_t) keys.size ();
  out->head_gap_sec = keys.front ();
  out->tail_gap_sec = tail;
  std::sort (gaps.begin (), gaps.end ());
  out->median_gap_sec = gop_percentile (gaps, 0.5);
  out->p95_gap_sec    = gop_percentile (gaps, 0.95);
  out->max_gap_sec    = gaps.back ();
  return TCS_OK;
}

} /* extern C */

