/* tcs_gstreamer implementation (v3). See tcs_gstreamer.h for the contract.
 *
 * Role: GPU frame SOURCE for the compositing layer.
 *   filesrc ! typefind ! demux ! <explicit per-codec chain> ! appsink
 * - decodebin is NOT used for video (it downloads d3d11 pads to sysmem and
 *   would auto-pick CPU decoders). Codecs are classified explicitly; unknown
 *   codecs are refused unless TCS_ALLOW_UNKNOWN=1 (debug). video/x-hap is
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

#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>

#include <gst/gst.h>
#include <gst/app/gstappsink.h>
#include <gst/video/video.h>
#include <gst/d3d11/gstd3d11.h>

#include "SpoutDX.h"

#include <mutex>
#include <string>
#include <atomic>
#include <thread>
#include <cstdio>
#include <cstdarg>
#include <cstring>

#define LOG(fmt, ...) fprintf (stderr, "[tcs-gst] " fmt "\n", ##__VA_ARGS__)

static std::once_flag g_gst_once;

static bool
env_flag (const char* name)
{
  char buf[8];
  return GetEnvironmentVariableA (name, buf, sizeof (buf)) > 0;
}

struct TcsPlayer {
  /* D3D11 + Spout */
  ID3D11Device* device = nullptr;
  bool owns_device = false;
  ID3D11DeviceContext* context = nullptr;
  GstD3D11Device* gst_dev = nullptr;      /* wrapped device, ref'd for lifetime */
  spoutDX* spout = nullptr;               /* verification layer only */
  bool spout_ready = false;
  std::string sender_name;

  /* GStreamer (UI-thread structural changes only) */
  GstElement* pipeline = nullptr;
  GstElement* typefind = nullptr;
  GstElement* demux = nullptr;
  GstElement* appsink = nullptr;
  GstElement* vcaps = nullptr;
  bool vchain_built = false;
  GstElement* aqueue = nullptr;
  GstElement* avolume = nullptr;
  GstElement* aconvert = nullptr;
  GstElement* asink = nullptr;
  gboolean use_d3d11_caps = FALSE;

  /* frame slots (frame_lock) */
  std::mutex frame_lock;
  GstSample* latest = nullptr;            /* newest sample, unreleased yet */
  uint64_t latest_gen = 0;
  uint64_t latest_pts_ns = 0;
  uint64_t latest_seq = 0;
  bool latest_gpu = false;
  GstSample* leased = nullptr;            /* held by the compositor */
  TcsFrameInfo lease_info = {};
  bool pending_update = false;
  uint64_t generation = 1;                /* bumped by owner on load/seek */
  guint64 frames_decoded = 0;
  guint64 spout_sends = 0;

  /* callback */
  tcs_frame_notify_fn notify = nullptr;
  void* notify_user = nullptr;

  /* stream info (frame_lock) */
  std::string path;
  std::string decoder_name;
  std::string last_error;
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

  /* bus thread */
  std::atomic<bool> bus_running{false};
  std::thread bus_thread;

  /* D3D helpers (frame_lock held) */
  ID3D11Texture2D* staging_read = nullptr;
  D3D11_TEXTURE2D_DESC staging_desc = {};
  ID3D11Texture2D* single_tex = nullptr;
  D3D11_TEXTURE2D_DESC single_desc = {};
};

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

static void
demote_foreign_gpu_decoders (void)
{
  /* decodebin is only reachable for audio and debug fallbacks; keep CUDA /
   * D3D12 decoders out of its choices (their buffers do not share our
   * D3D11 device). */
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

static gboolean
create_or_adopt_device (TcsPlayer* p, ID3D11Device* external)
{
  if (external) {
    p->device = external;
    p->device->AddRef ();
    p->device->GetImmediateContext (&p->context);
    p->owns_device = false;
  } else {
    UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
    HRESULT hr = D3D11CreateDevice (nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
        levels, 2, D3D11_SDK_VERSION, &p->device, nullptr, &p->context);
    if (FAILED (hr)) {
      set_error (p, "D3D11CreateDevice failed hr=0x%08lx", hr);
      return FALSE;
    }
    p->owns_device = true;
  }
  if (!p->context) {
    set_error (p, "no immediate context");
    return FALSE;
  }
  /* external devices may not have been created with multithread protection;
   * enable it (idempotent) since GStreamer streams from other threads. */
  ID3D11Multithread* mt = nullptr;
  if (SUCCEEDED (p->context->QueryInterface (__uuidof(ID3D11Multithread), (void**) &mt)) && mt) {
    mt->SetMultithreadProtected (TRUE);
    mt->Release ();
  }
  /* the device must expose the video interfaces for d3d11 dec/convert */
  ID3D11VideoDevice* vd = nullptr;
  if (FAILED (p->device->QueryInterface (__uuidof(ID3D11VideoDevice), (void**) &vd)) || !vd) {
    set_error (p, "external device lacks ID3D11VideoDevice (needs D3D11_CREATE_DEVICE_VIDEO_SUPPORT)");
    return FALSE;
  }
  vd->Release ();

  p->gst_dev = gst_d3d11_device_new_wrapped (p->device);
  if (!p->gst_dev) {
    set_error (p, "gst_d3d11_device_new_wrapped failed");
    return FALSE;
  }
  return TRUE;
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

/* ---------------- frame delivery ---------------- */

static GstFlowReturn
on_new_sample (GstAppSink* sink, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  GstSample* sample = gst_app_sink_try_pull_sample (sink, 0);
  if (!sample)
    return GST_FLOW_OK;

  GstBuffer* buf = gst_sample_get_buffer (sample);
  GstMemory* mem = buf ? gst_buffer_peek_memory (buf, 0) : nullptr;
  bool gpu = mem && gst_is_d3d11_memory (mem);
  const GstSegment* seg = gst_sample_get_segment (sample);
  guint64 pts = buf && GST_BUFFER_PTS (buf) != GST_CLOCK_TIME_NONE
      ? GST_BUFFER_PTS (buf)
      : (seg ? (guint64) seg->position : 0);

  tcs_frame_notify_fn cb = nullptr;
  void* cb_user = nullptr;
  uint64_t cb_gen = 0, cb_seq = 0;
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    if (p->latest)
      gst_sample_unref (p->latest);
    p->latest = sample;
    p->latest_gen = p->generation;
    p->latest_pts_ns = pts;
    p->latest_seq = ++p->frames_decoded;
    p->latest_gpu = gpu;
    p->pending_update = true;
    cb = p->notify;
    cb_user = p->notify_user;
    cb_gen = p->latest_gen;
    cb_seq = p->latest_seq;
  }
  if (cb)
    cb (cb_user, cb_gen, cb_seq);
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
 * Classification is a table so new branches (e.g. video/x-hap as a
 * compressed-texture passthrough, out of scope for now) can be added
 * WITHOUT ever falling into a CPU decoder automatically. */

enum StreamClass {
  STREAM_H264, STREAM_H265, STREAM_VP9, STREAM_AV1,
  STREAM_RAW_VIDEO,
  STREAM_HAP,             /* reserved: must never auto-map to avdec_hap */
  STREAM_UNKNOWN
};

static StreamClass
classify_video_stream (GstCaps* caps)
{
  const char* mt = caps_first_media (caps);
  if (!mt)
    return STREAM_UNKNOWN;
  if (g_str_has_prefix (mt, "video/x-h264")) return STREAM_H264;
  if (g_str_has_prefix (mt, "video/x-h265") || g_str_has_prefix (mt, "video/x-hevc")) return STREAM_H265;
  if (g_str_has_prefix (mt, "video/x-vp9")) return STREAM_VP9;
  if (g_str_has_prefix (mt, "video/x-av1")) return STREAM_AV1;
  if (g_str_has_prefix (mt, "video/x-raw")) return STREAM_RAW_VIDEO;
  if (g_str_has_prefix (mt, "video/x-hap")) return STREAM_HAP;
  return STREAM_UNKNOWN;
}

static bool
factory_exists (const char* name)
{
  GstElementFactory* gf = gst_element_factory_find (name);
  if (gf) { gst_object_unref (gf); return true; }
  return false;
}

/* Create appsink + capsfilter for the tail. d3d selects D3D11Memory BGRA. */
static void
create_appsink_tail (TcsPlayer* p, gboolean d3d)
{
  p->vcaps = gst_element_factory_make ("capsfilter", nullptr);
  p->appsink = gst_element_factory_make ("appsink", nullptr);
  GstCaps* caps = gst_caps_from_string (
      d3d ? "video/x-raw(memory:D3D11Memory),format=BGRA"
          : "video/x-raw,format=BGRA");
  g_object_set (p->vcaps, "caps", caps, nullptr);
  gst_caps_unref (caps);
  GstAppSinkCallbacks cbs = {};
  cbs.new_sample = on_new_sample;
  gst_app_sink_set_callbacks (GST_APP_SINK (p->appsink), &cbs, p, nullptr);
  g_object_set (p->appsink, "emit-signals", TRUE, "sync", TRUE, "drop", FALSE,
      "max-buffers", 4, nullptr);
  p->use_d3d11_caps = d3d;
}

static gboolean
build_video_chain (TcsPlayer* p, GstPad* pad, GstCaps* caps)
{
  if (p->vchain_built)
    return TRUE;
  p->vchain_built = TRUE;

  StreamClass sc = classify_video_stream (caps);
  record_video_caps (p, caps);

  /* Reserved classes must fail cleanly, not fall back to a CPU decoder. */
  if (sc == STREAM_HAP) {
    set_error (p, "video/x-hap requires the dedicated compressed-texture branch (not implemented; refusing avdec)");
    return FALSE;
  }
  if (sc == STREAM_UNKNOWN && !env_flag ("TCS_ALLOW_UNKNOWN")) {
    const char* mt = caps_first_media (caps);
    set_error (p, "no explicit video branch for stream type '%s' (set TCS_ALLOW_UNKNOWN=1 to use decodebin for debugging)", mt ? mt : "?");
    return FALSE;
  }

  const char* parse = nullptr;
  const char* dec_gpu = nullptr;
  const char* dec_cpu = nullptr;
  const char* chosen_dec = nullptr;
  gboolean want_gpu = FALSE;
  GstElement *e_parse = nullptr, *e_dec = nullptr, *e_conv = nullptr, *e_down = nullptr;

  switch (sc) {
    case STREAM_H264: parse = "h264parse"; dec_gpu = "d3d11h264dec"; dec_cpu = "avdec_h264"; break;
    case STREAM_H265: parse = "hevcparse"; dec_gpu = "d3d11h265dec"; dec_cpu = "avdec_h265"; break;
    case STREAM_VP9:  parse = "vp9parse";  dec_gpu = "d3d11vp9dec";  dec_cpu = "avdec_vp9"; break;
    case STREAM_AV1:   parse = "av1parse";  dec_gpu = "d3d11av1dec";  dec_cpu = "dav1ddec"; break;
    case STREAM_RAW_VIDEO: break;
    default: break; /* unknown debug mode -> decodebin below */
  }

  if (dec_gpu && factory_exists (dec_gpu) && factory_exists ("d3d11colorconvert"))
    want_gpu = TRUE;
  else if (dec_cpu && factory_exists (dec_cpu))
    want_gpu = FALSE;
  else if (sc != STREAM_RAW_VIDEO && !env_flag ("TCS_ALLOW_UNKNOWN")) {
    set_error (p, "required decoders for stream class %d unavailable", (int) sc);
    return FALSE;
  }

  if (sc == STREAM_UNKNOWN) {
    /* debug-only generic path (never for HAP) */
    e_down = gst_element_factory_make ("decodebin", nullptr);
    e_conv = gst_element_factory_make ("videoconvert", nullptr);
    if (!e_down || !e_conv) { set_error (p, "fallback factories failed"); return FALSE; }
    create_appsink_tail (p, FALSE);
    gst_bin_add_many (GST_BIN (p->pipeline), e_down, e_conv, p->vcaps, p->appsink, nullptr);
    if (!gst_element_link_many (e_down, e_conv, p->vcaps, p->appsink, nullptr)) {
      set_error (p, "fallback chain link failed"); return FALSE;
    }
    GstPad* sp = gst_element_get_static_pad (e_down, "sink");
    gst_pad_link_full (pad, sp, GST_PAD_LINK_CHECK_NOTHING);
    gst_object_unref (sp);
    give_device_context (p, p->pipeline);
    {
      std::lock_guard<std::mutex> g (p->frame_lock);
      p->decoder_name = "decodebin(debug-fallback)";
    }
    LOG ("video chain: DEBUG FALLBACK decodebin ! videoconvert ! BGRA");
    return TRUE;
  }

  if (parse && factory_exists (parse))
    e_parse = gst_element_factory_make (parse, nullptr);

  if (want_gpu) {
    e_dec = gst_element_factory_make (dec_gpu, nullptr);
    e_conv = gst_element_factory_make ("d3d11colorconvert", nullptr);
    chosen_dec = dec_gpu;
  } else if (dec_cpu) {
    e_dec = gst_element_factory_make (dec_cpu, nullptr);
    e_conv = gst_element_factory_make ("videoconvert", nullptr);
    chosen_dec = dec_cpu;
  } else {
    e_conv = gst_element_factory_make ("videoconvert", nullptr); /* raw pad */
    chosen_dec = "raw";
  }
  if (!e_conv || (dec_cpu && !e_dec)) {
    set_error (p, "chain element factory failed");
    return FALSE;
  }

  create_appsink_tail (p, want_gpu);

  GstElement* chain[] = { e_parse, e_dec, e_conv, p->vcaps, p->appsink, nullptr };
  for (int i = 0; chain[i]; i++)
    gst_bin_add (GST_BIN (p->pipeline), chain[i]);
  for (int i = 0; chain[i]; i++)
    gst_element_sync_state_with_parent (chain[i]);
  for (int i = 0; chain[i + 1]; i++) {
    if (!gst_element_link (chain[i], chain[i + 1])) {
      set_error (p, "manual chain link failed at position %d", i);
      return FALSE;
    }
  }
  for (int i = 0; chain[i]; i++)
    give_device_context (p, chain[i]);
  give_device_context (p, p->pipeline);

  GstPad* head_sink = gst_element_get_static_pad (chain[0], "sink");
  GstPadLinkReturn lr = head_sink ? gst_pad_link (pad, head_sink) : GST_PAD_LINK_REFUSED;
  if (head_sink)
    gst_object_unref (head_sink);
  if (lr != GST_PAD_LINK_OK) {
    set_error (p, "demux pad -> video chain head link failed (%d)", lr);
    return FALSE;
  }
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->decoder_name = chosen_dec ? chosen_dec : "?";
  }
  LOG ("video chain %s: %s %s %s BGRA appsink", want_gpu ? "GPU" : "CPU",
      parse ? parse : "", chosen_dec ? chosen_dec : "", want_gpu ? "d3d11colorconvert" : "videoconvert");
  return TRUE;
}

static gboolean
build_audio_chain (TcsPlayer* p, GstPad* pad, GstCaps* caps)
{
  const char* mt = caps_first_media (caps);
  gboolean raw = mt && g_str_has_prefix (mt, "audio/x-raw");

  p->aqueue = gst_element_factory_make ("queue", nullptr);
  p->avolume = gst_element_factory_make ("volume", nullptr);
  p->aconvert = gst_element_factory_make ("audioconvert", nullptr);
  p->asink = gst_element_factory_make ("autoaudiosink", nullptr);
  if (!p->aqueue || !p->avolume || !p->aconvert || !p->asink)
    return FALSE;
  g_object_set (p->avolume, "volume", p->muted ? 0.0 : p->volume_value / 100.0, nullptr);
  gst_bin_add_many (GST_BIN (p->pipeline), p->aqueue, p->avolume, p->aconvert, p->asink, nullptr);
  if (!gst_element_link_many (p->aqueue, p->avolume, p->aconvert, p->asink, nullptr)) {
    set_error (p, "audio chain link failed");
    return FALSE;
  }
  GstElement* head = p->aqueue;
  if (!raw) {
    /* audio decode is fine via decodebin (no GPU memory concerns here) */
    GstElement* dbin = gst_element_factory_make ("decodebin", nullptr);
    if (!dbin)
      return FALSE;
    gst_bin_add (GST_BIN (p->pipeline), dbin);
    if (!gst_element_link (dbin, p->aqueue)) {
      set_error (p, "audio decodebin link failed");
      return FALSE;
    }
    head = dbin;
  }
  GstPad* sink_pad = gst_element_get_static_pad (head, "sink");
  GstPadLinkReturn lr = gst_pad_link_full (pad, sink_pad, GST_PAD_LINK_CHECK_NOTHING);
  gst_object_unref (sink_pad);
  if (lr != GST_PAD_LINK_OK)
    set_error (p, "audio pad link failed (%d)", lr);
  gst_element_sync_state_with_parent (p->aqueue);
  gst_element_sync_state_with_parent (p->avolume);
  gst_element_sync_state_with_parent (p->aconvert);
  gst_element_sync_state_with_parent (p->asink);
  return lr == GST_PAD_LINK_OK;
}

static void on_demux_pad_added (GstElement*, GstPad*, gpointer);

static void
on_have_type (GstElement* /*typefind*/, guint, GstCaps* caps, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  const char* mt = caps_first_media (caps);
  const char* demux = nullptr;
  if (mt) {
    if (g_str_has_prefix (mt, "video/quicktime") || g_str_has_prefix (mt, "video/mp4") ||
        g_str_has_prefix (mt, "video/x-m4v") || g_str_has_prefix (mt, "video/3gpp"))
      demux = "qtdemux";
    else if (g_str_has_prefix (mt, "video/x-matroska") || g_str_has_prefix (mt, "audio/x-matroska"))
      demux = "matroskademux";
    else if (g_str_has_prefix (mt, "video/mpegts") || g_str_has_prefix (mt, "video/x-mp2t"))
      demux = "tsdemux";
    else if (g_str_has_prefix (mt, "video/x-mxf"))
      demux = "mxfdemux";
    else if (g_str_has_prefix (mt, "video/x-msvideo") || g_str_has_prefix (mt, "video/avi"))
      demux = "avimux";
    else if (g_str_has_prefix (mt, "video/x-h264") || g_str_has_prefix (mt, "video/x-h265") ||
             g_str_has_prefix (mt, "video/x-raw") || g_str_has_prefix (mt, "video/x-vp9") ||
             g_str_has_prefix (mt, "video/x-av1"))
      demux = nullptr; /* elementary stream: feed the video chain directly */
  }

  GstPad* tf_src = gst_element_get_static_pad (p->typefind, "src");
  if (!demux) {
    if (mt && g_str_has_prefix (mt, "video/")) {
      build_video_chain (p, tf_src, caps);
      gst_object_unref (tf_src);
      return;
    }
    if (!env_flag ("TCS_ALLOW_UNKNOWN")) {
      set_error (p, "no container branch for type '%s' (TCS_ALLOW_UNKNOWN=1 enables decodebin debugging)", mt ? mt : "?");
      gst_object_unref (tf_src);
      return;
    }
    demux = "decodebin"; /* debug only */
  }

  p->demux = gst_element_factory_make (demux, nullptr);
  if (!p->demux) {
    set_error (p, "demux factory %s failed", demux);
    gst_object_unref (tf_src);
    return;
  }
  gst_bin_add (GST_BIN (p->pipeline), p->demux);
  gst_element_sync_state_with_parent (p->demux);
  GstPad* dsink = gst_element_get_static_pad (p->demux, "sink");
  if (tf_src && dsink && gst_pad_link (tf_src, dsink) != GST_PAD_LINK_OK)
    set_error (p, "typefind->demux link failed");
  if (dsink) gst_object_unref (dsink);
  if (tf_src) gst_object_unref (tf_src);
  g_signal_connect (p->demux, "pad-added", G_CALLBACK (on_demux_pad_added), p);
  LOG ("type=%s demux=%s", mt ? mt : "?", demux);
}

static void
on_demux_pad_added (GstElement* /*demux*/, GstPad* pad, gpointer user)
{
  TcsPlayer* p = (TcsPlayer*) user;
  GstCaps* caps = gst_pad_get_current_caps (pad);
  if (!caps)
    caps = gst_pad_query_caps (pad, nullptr);
  const char* mt = caps_first_media (caps);
  gboolean is_video = mt && g_str_has_prefix (mt, "video/");
  gboolean is_audio = mt && g_str_has_prefix (mt, "audio/");
  LOG ("demux pad '%s' caps=%s", GST_PAD_NAME (pad), mt ? mt : "(none)");
  if (is_video)
    build_video_chain (p, pad, caps);
  else if (is_audio)
    build_audio_chain (p, pad, caps);
  if (caps)
    gst_caps_unref (caps);
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
    if (p->latest) { gst_sample_unref (p->latest); p->latest = nullptr; }
    if (p->leased) { gst_sample_unref (p->leased); p->leased = nullptr; }
    p->pending_update = false;
    p->frames_decoded = 0;
  }

  if (p->pipeline) {
    gst_element_set_state (p->pipeline, GST_STATE_NULL);
    gst_object_unref (p->pipeline);
    p->pipeline = nullptr;
  }
  p->typefind = nullptr;
  p->demux = nullptr;
  p->appsink = nullptr;
  p->vcaps = nullptr;
  p->vchain_built = false;
  p->aqueue = nullptr;
  p->avolume = nullptr;
  p->aconvert = nullptr;
  p->asink = nullptr;
}

static int
build_pipeline (TcsPlayer* p, const char* utf8_path, double start_sec, int paused)
{
  teardown_pipeline (p);

  p->failed = false;
  p->eos = false;
  p->duration = -1.0;
  p->width = p->height = 0;
  p->fps = 0.0;
  p->use_d3d11_caps = FALSE;
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->decoder_name.clear ();
    p->generation++; /* new media => new source/seek generation */
  }

  p->pipeline = gst_pipeline_new ("tcs_play");
  GstElement* src = gst_element_factory_make ("filesrc", nullptr);
  p->typefind = gst_element_factory_make ("typefind", nullptr);
  if (!p->pipeline || !src || !p->typefind) {
    if (p->pipeline) { gst_object_unref (p->pipeline); p->pipeline = nullptr; }
    set_error (p, "element factory failed");
    return TCS_ERR_GENERIC;
  }
  g_object_set (src, "location", utf8_path, nullptr);

  gst_bin_add_many (GST_BIN (p->pipeline), src, p->typefind, nullptr);
  if (!gst_element_link (src, p->typefind)) {
    teardown_pipeline (p);
    set_error (p, "src->typefind link failed");
    return TCS_ERR_GENERIC;
  }
  g_signal_connect (p->typefind, "have-type", G_CALLBACK (on_have_type), p);
  give_device_context (p, p->pipeline);

  GstStateChangeReturn scr = gst_element_set_state (p->pipeline, GST_STATE_PAUSED);
  if (scr == GST_STATE_CHANGE_FAILURE) {
    teardown_pipeline (p);
    set_error (p, "set_state PAUSED failed");
    return TCS_ERR_NOT_LOADED;
  }

  p->bus_running = true;
  p->bus_thread = std::thread (bus_loop, p);

  if (scr == GST_STATE_CHANGE_ASYNC)
    gst_element_get_state (p->pipeline, nullptr, nullptr, 5 * GST_SECOND);

  bool got_frame = false;
  for (int i = 0; i < 100; i++) {
    bool done;
    {
      std::lock_guard<std::mutex> g (p->frame_lock);
      done = p->frames_decoded > 0 || p->failed;
    }
    if (done)
      break;
    Sleep (50);
  }
  {
    std::lock_guard<std::mutex> g (p->frame_lock);
    got_frame = p->frames_decoded > 0 && !p->failed;
  }
  if (!got_frame) {
    std::lock_guard<std::mutex> g (p->frame_lock);
    std::string err = p->last_error;
    teardown_pipeline (p);
    if (err.empty ())
      err = "preroll timed out";
    p->last_error = err;
    LOG ("load failed: %s", err.c_str ());
    return TCS_ERR_NOT_LOADED;
  }

  if (start_sec > 0.0) {
    std::lock_guard<std::mutex> g (p->frame_lock);
    p->eos = false;
    p->generation++;
    gst_element_seek (p->pipeline, 1.0, GST_FORMAT_TIME,
        (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE),
        GST_SEEK_TYPE_SET, (gint64) (start_sec * GST_SECOND), GST_SEEK_TYPE_NONE, -1);
  }

  p->path = utf8_path;
  p->paused = paused != 0;

  if (!paused) {
    std::lock_guard<std::mutex> g (p->frame_lock);
    gst_element_set_state (p->pipeline, GST_STATE_PLAYING);
  }

  gint64 q = 0;
  if (gst_element_query_duration (p->pipeline, GST_FORMAT_TIME, &q) && q > 0)
    p->duration = (double) q / GST_SECOND;

  LOG ("loaded %s decoder=%s %dx%d@%.3f mem=%s", utf8_path,
      p->decoder_name.empty () ? "?" : p->decoder_name.c_str (),
      p->width, p->height, p->fps, p->use_d3d11_caps ? "d3d11" : "sysmem");
  return TCS_OK;
}

/* ---------------- D3D helpers (frame_lock held) ---------------- */

static ID3D11Texture2D*
ensure_staging (TcsPlayer* p, const D3D11_TEXTURE2D_DESC* src_desc)
{
  if (p->staging_read &&
      p->staging_desc.Width == src_desc->Width &&
      p->staging_desc.Height == src_desc->Height &&
      p->staging_desc.Format == src_desc->Format)
    return p->staging_read;

  if (p->staging_read) { p->staging_read->Release (); p->staging_read = nullptr; }

  D3D11_TEXTURE2D_DESC d = *src_desc;
  d.Usage = D3D11_USAGE_STAGING;
  d.BindFlags = 0;
  d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
  d.MiscFlags = 0;
  d.ArraySize = 1;
  d.MipLevels = 1;
  d.SampleDesc.Count = 1;
  if (FAILED (p->device->CreateTexture2D (&d, nullptr, &p->staging_read)))
    return nullptr;
  p->staging_desc = d;
  return p->staging_read;
}

static ID3D11Texture2D*
texture_of_lease (TcsPlayer* p, guint* sub_out, ID3D11Texture2D** owned_out)
{
  *owned_out = nullptr;
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
    *sub_out = 0;
    return tex; /* caller releases */
  }
  /* array texture: flatten into our single texture */
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
  *owned_out = p->single_tex; /* marker: returned tex is owned by player */
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

  demote_foreign_gpu_decoders ();

  if (!create_or_adopt_device (p, (ID3D11Device*) external_d3d11_device)) {
    if (errbuf && errbuf_len)
      snprintf (errbuf, errbuf_len, "%s", p->last_error.c_str ());
    if (p->gst_dev) gst_object_unref (p->gst_dev);
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
  if (p->staging_read) p->staging_read->Release ();
  if (p->single_tex) p->single_tex->Release ();
  if (p->spout) {
    p->spout->ReleaseSender ();
    p->spout->CloseDirectX11 ();
    delete p->spout;
  }
  if (p->gst_dev) gst_object_unref (p->gst_dev);
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
  std::lock_guard<std::mutex> g (player->frame_lock);
  player->paused = paused != 0;
  if (player->pipeline)
    gst_element_set_state (player->pipeline,
        paused ? GST_STATE_PAUSED : GST_STATE_PLAYING);
  return TCS_OK;
}

TCS_GST_API int
tcs_player_get_paused (TcsPlayer* player)
{
  return player ? (player->paused ? 1 : 0) : -1;
}

static uint64_t
seek_locked (TcsPlayer* p, double seconds)
{
  if (!p->pipeline)
    return p->generation;
  p->generation++;
  if (p->eos) {
    p->eos = false;
    if (!p->paused)
      gst_element_set_state (p->pipeline, GST_STATE_PLAYING);
  }
  gst_element_seek (p->pipeline, p->rate, GST_FORMAT_TIME,
      (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE),
      GST_SEEK_TYPE_SET, (gint64) (seconds * GST_SECOND), GST_SEEK_TYPE_NONE, -1);
  return p->generation;
}

TCS_GST_API uint64_t
tcs_player_seek (TcsPlayer* player, double seconds)
{
  if (!player) return 0;
  std::lock_guard<std::mutex> g (player->frame_lock);
  return seek_locked (player, seconds);
}

TCS_GST_API uint64_t
tcs_player_step_frame (TcsPlayer* player)
{
  if (!player) return 0;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (!player->pipeline) return player->generation;
  gint64 pos = 0;
  gst_element_query_position (player->pipeline, GST_FORMAT_TIME, &pos);
  double step = player->fps > 0.0 ? 1.0 / player->fps : 0.04;
  return seek_locked (player, (double) pos / GST_SECOND + step);
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
  std::lock_guard<std::mutex> g (player->frame_lock);
  player->rate = rate > 0.0 ? rate : 1.0;
  if (!player->pipeline) return TCS_OK;
  gint64 pos = 0;
  gst_element_query_position (player->pipeline, GST_FORMAT_TIME, &pos);
  gst_element_seek (player->pipeline, player->rate, GST_FORMAT_TIME,
      (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE | GST_SEEK_FLAG_SKIP),
      GST_SEEK_TYPE_SET, pos, GST_SEEK_TYPE_NONE, -1);
  return TCS_OK;
}

static void
apply_volume_locked (TcsPlayer* p)
{
  if (p->avolume)
    g_object_set (p->avolume, "volume",
        p->muted ? 0.0 : p->volume_value / 100.0, nullptr);
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

TCS_GST_API int
tcs_player_get_time_pos (TcsPlayer* player, double* out_sec)
{
  if (!player || !out_sec) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (!player->pipeline || player->path.empty ()) return TCS_ERR_NOT_LOADED;
  gint64 pos = 0;
  if (!gst_element_query_position (player->pipeline, GST_FORMAT_TIME, &pos) || pos < 0)
    return TCS_ERR_NOT_LOADED;
  *out_sec = (double) pos / GST_SECOND;
  return TCS_OK;
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
  std::lock_guard<std::mutex> g (player->frame_lock);
  TcsPlayer* p = player;

  /* Already leased frame of this generation and not stale: keep it. */
  if (p->leased && p->lease_info.generation == generation) {
    *out_info = p->lease_info;
    return 1;
  }
  /* Drop a lease from an older generation? No: the compositor owns it until
   * it releases. If a lease is still held, refuse silently (none). */
  if (p->leased)
    return 0;

  if (!p->latest || p->latest_gen != generation)
    return 0;

  p->leased = p->latest;   /* take ownership; pool texture now pinned */
  p->latest = nullptr;
  memset (&p->lease_info, 0, sizeof (p->lease_info));
  p->lease_info.generation = p->latest_gen;
  p->lease_info.seq = p->latest_seq;
  p->lease_info.pts_ns = (int64_t) p->latest_pts_ns;
  p->lease_info.width = p->width;
  p->lease_info.height = p->height;
  p->lease_info.is_gpu = p->latest_gpu ? 1 : 0;
  *out_info = p->lease_info;
  return 1;
}

TCS_GST_API int
tcs_player_leased_texture (TcsPlayer* player, void** out_texture,
                           uint32_t* out_subresource, uint32_t* out_dxgi_format)
{
  if (!player || !out_texture) return TCS_ERR_NO_FRAME;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (!player->leased) return TCS_ERR_NO_FRAME;
  guint sub = 0;
  ID3D11Texture2D* owned = nullptr;
  ID3D11Texture2D* tex = texture_of_lease (player, &sub, &owned);
  (void) owned;
  if (!tex) return TCS_ERR_NO_FRAME;
  D3D11_TEXTURE2D_DESC desc;
  tex->GetDesc (&desc);
  tex->Release ();
  *out_texture = tex;              /* valid until release() */
  if (out_subresource) *out_subresource = sub;
  if (out_dxgi_format) *out_dxgi_format = (uint32_t) desc.Format;
  return TCS_OK;
}

TCS_GST_API int
tcs_player_leased_cpu_copy (TcsPlayer* player, uint8_t* dst, int dst_stride)
{
  if (!player || !dst) return TCS_ERR_GENERIC;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (!player->leased) return TCS_ERR_NO_FRAME;
  GstBuffer* buf = gst_sample_get_buffer (player->leased);
  GstMemory* mem = buf ? gst_buffer_peek_memory (buf, 0) : nullptr;
  if (!mem) return TCS_ERR_NO_FRAME;
  int w = player->lease_info.width, h = player->lease_info.height;
  if (w <= 0 || h <= 0) return TCS_ERR_NO_FRAME;

  if (gst_is_d3d11_memory (mem)) {
    GstD3D11Memory* dmem = (GstD3D11Memory*) mem;
    ID3D11Resource* res = gst_d3d11_memory_get_resource_handle (dmem);
    guint sub = gst_d3d11_memory_get_subresource_index (dmem);
    if (!res) return TCS_ERR_NO_FRAME;
    ID3D11Texture2D* tex = nullptr;
    if (FAILED (res->QueryInterface (__uuidof(ID3D11Texture2D), (void**) &tex)) || !tex)
      return TCS_ERR_NO_FRAME;
    D3D11_TEXTURE2D_DESC desc;
    tex->GetDesc (&desc);
    int rc = TCS_ERR_GENERIC;
    ID3D11Texture2D* staging = ensure_staging (player, &desc);
    if (staging) {
      if (desc.ArraySize == 1 && sub == 0)
        player->context->CopyResource (staging, tex);
      else {
        D3D11_BOX box = {};
        box.right = desc.Width;
        box.bottom = desc.Height;
        box.back = 1;
        player->context->CopySubresourceRegion (staging, 0, 0, 0, 0, tex, sub, &box);
      }
      player->context->Flush ();
      D3D11_MAPPED_SUBRESOURCE map;
      if (SUCCEEDED (player->context->Map (staging, 0, D3D11_MAP_READ, 0, &map))) {
        int row = (int) MIN ((UINT) dst_stride, map.RowPitch);
        for (int y = 0; y < h; y++)
          memcpy (dst + (size_t) y * dst_stride,
              (uint8_t*) map.pData + (size_t) y * map.RowPitch, row);
        player->context->Unmap (staging, 0);
        rc = TCS_OK;
      }
    }
    tex->Release ();
    return rc;
  }

  GstMapInfo info;
  if (!gst_buffer_map (buf, &info, GST_MAP_READ))
    return TCS_ERR_NO_FRAME;
  gsize src_stride = (gsize) w * 4;
  GstVideoMeta* meta = gst_buffer_get_video_meta (buf);
  if (meta) src_stride = meta->stride[0];
  int row = (int) MIN ((gsize) dst_stride, src_stride);
  for (int y = 0; y < h; y++)
    memcpy (dst + (size_t) y * dst_stride, info.data + (size_t) y * src_stride, row);
  gst_buffer_unmap (buf, &info);
  return TCS_OK;
}

TCS_GST_API void
tcs_player_release (TcsPlayer* player)
{
  if (!player) return;
  std::lock_guard<std::mutex> g (player->frame_lock);
  if (player->leased) {
    gst_sample_unref (player->leased);   /* returns the pool texture */
    player->leased = nullptr;
  }
}

/* ---- verification layer ---- */

TCS_GST_API int
tcs_player_publish_spout (TcsPlayer* player)
{
  if (!player) return TCS_ERR_GENERIC;
  if (!player->spout_ready) return TCS_ERR_SPOUT;
  std::lock_guard<std::mutex> g (player->frame_lock);
  GstSample* sample = player->leased ? player->leased : player->latest;
  if (!sample) return TCS_ERR_NO_FRAME;
  GstBuffer* buf = gst_sample_get_buffer (sample);
  GstMemory* mem = buf ? gst_buffer_peek_memory (buf, 0) : nullptr;
  bool ok = false;

  if (mem && gst_is_d3d11_memory (mem)) {
    guint sub = 0;
    ID3D11Texture2D* owned = nullptr;
    GstSample* keep = player->leased;
    player->leased = sample; /* texture_of_lease reads the lease slot */
    ID3D11Texture2D* tex = texture_of_lease (player, &sub, &owned);
    player->leased = keep;
    if (tex) {
      ok = player->spout->SendTexture (tex);
      tex->Release ();
    }
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

} /* extern C */

