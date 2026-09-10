// tcs-gst-proto: standalone prototype for GStreamer -> D3D11 -> Spout2 path.
// Modes:
//   tcs-gst-proto send  <file> <seconds> [sendername] [seek_at_sec]
//   tcs-gst-proto recv  [sendername] [seconds] [out-bmp-prefix]
//
// Verification goals:
//  - actual decoder selection (manual pipeline, no decodebin ambiguity)
//  - device identity: GStreamer d3d11 device == our ID3D11Device == SpoutDX device
//  - frame memory is video/x-raw(memory:D3D11Memory) i.e. no CPU readback before SendTexture
//  - seek while playing (post-seek PTS + frame continues)
//  - clean shutdown

#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <gst/gst.h>
#include <gst/app/gstappsink.h>
#include <gst/d3d11/gstd3d11.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

#include "SpoutDX.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

static ID3D11Device *g_device = nullptr;
static ID3D11DeviceContext *g_context = nullptr;

static bool
create_d3d_device (WCHAR *adapter_name, size_t adapter_name_len)
{
  UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#ifdef _DEBUG
  flags |= D3D11_CREATE_DEVICE_DEBUG;
#endif
  D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
  D3D_FEATURE_LEVEL got;
  HRESULT hr = D3D11CreateDevice (nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
      levels, 2, D3D11_SDK_VERSION, &g_device, &got, &g_context);
  if (FAILED (hr)) {
    // retry without debug layer
    hr = D3D11CreateDevice (nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        flags & ~D3D11_CREATE_DEVICE_DEBUG, levels, 2, D3D11_SDK_VERSION,
        &g_device, &got, &g_context);
  }
  if (FAILED (hr)) {
    printf ("[ERR] D3D11CreateDevice hr=0x%08lx\n", hr);
    return false;
  }

  // Make the context thread-safe (GStreamer uses it from streaming threads).
  ID3D11Multithread *mt = nullptr;
  if (SUCCEEDED (g_context->QueryInterface (__uuidof(ID3D11Multithread), (void**) &mt)) && mt) {
    mt->SetMultithreadProtected (TRUE);
    mt->Release ();
  } else {
    printf ("[WARN] ID3D11Multithread unavailable\n");
  }

  IDXGIDevice *dxgi_dev = nullptr;
  if (SUCCEEDED (g_device->QueryInterface (__uuidof(IDXGIDevice), (void**) &dxgi_dev)) && dxgi_dev) {
    IDXGIAdapter *adapter = nullptr;
    if (SUCCEEDED (dxgi_dev->GetAdapter (&adapter)) && adapter) {
      DXGI_ADAPTER_DESC desc;
      if (SUCCEEDED (adapter->GetDesc (&desc))) {
        swprintf (adapter_name, adapter_name_len, L"%ls", desc.Description);
        wprintf (L"[GPU] device adapter=%ls feature_level=0x%x\n", adapter_name, got);
      }
      adapter->Release ();
    }
    dxgi_dev->Release ();
  }
  return true;
}

static GstContext *
build_d3d11_device_context (GstD3D11Device *gst_device)
{
  GstContext *context = gst_context_new (GST_D3D11_DEVICE_HANDLE_CONTEXT_TYPE, TRUE);
  GstStructure *s = gst_context_writable_structure (context);
  gst_structure_set (s, "device", GST_TYPE_D3D11_DEVICE, gst_device,
      "adapter", G_TYPE_UINT, 0u, nullptr);
  return context;
}

static void
set_message (GstBus *bus, GstMessage *msg, bool *error, std::string *err_desc, bool *eos)
{
  switch (GST_MESSAGE_TYPE (msg)) {
    case GST_MESSAGE_ERROR: {
      GError *e = nullptr;
      gst_message_parse_error (msg, &e, nullptr);
      *error = true;
      *err_desc = e ? e->message : "unknown";
      if (e) g_error_free (e);
      printf ("[ERR] bus error: %s\n", err_desc->c_str ());
      break;
    }
    case GST_MESSAGE_EOS:
      *eos = true;
      printf ("[BUS] EOS\n");
      break;
    default:
      break;
  }
}

static void
on_demux_pad_added (GstElement * /*demux*/, GstPad *pad, gpointer parse_sink)
{
  GstCaps *tmpl = gst_pad_get_allowed_caps (pad);
  gchar *tc = tmpl ? gst_caps_to_string (tmpl) : g_strdup("(none)");
  printf ("[PAD] qtdemux pad '%s' caps=%s\n", GST_PAD_NAME (pad), tc);
  g_free (tc);
  if (tmpl) gst_caps_unref (tmpl);
  gst_object_ref (parse_sink); // keep pad alive for the pipeline lifetime
  GstPadLinkReturn r = gst_pad_link_full (pad, (GstPad *) parse_sink, GST_PAD_LINK_CHECK_NOTHING);
  if (r != GST_PAD_LINK_OK)
    printf ("[ERR] pad-added link failed (%d)\n", r);
}

static int
do_send (const char *file, double seconds, const char *sender_name, double seek_at)
{
  WCHAR adapter_name[128] = L"";
  if (!create_d3d_device (adapter_name, 128))
    return 1;

  spoutDX spout;
  if (!spout.OpenDirectX11 (g_device)) {
    printf ("[ERR] SpoutDX::OpenDirectX11(our device) failed\n");
    return 1;
  }
  spout.SetSenderName (sender_name);
  printf ("[SPOUT] sender '%s' on our D3D11 device\n", sender_name);

  GstD3D11Device *gst_device = gst_d3d11_device_new_wrapped (g_device);
  if (!gst_device) {
    printf ("[ERR] gst_d3d11_device_new_wrapped failed\n");
    return 1;
  }
  printf ("[GST] wrapped our ID3D11Device as GstD3D11Device\n");

  GstElement *pipeline = gst_pipeline_new ("play");
  GstElement *filesrc = gst_element_factory_make ("filesrc", nullptr);
  GstElement *qtdemux = gst_element_factory_make ("qtdemux", nullptr);
  GstElement *parse = gst_element_factory_make ("h264parse", nullptr);
  GstElement *dec = gst_element_factory_make ("d3d11h264dec", nullptr);
  GstElement *convert = gst_element_factory_make ("d3d11colorconvert", nullptr);
  GstElement *caps = gst_element_factory_make ("capsfilter", nullptr);
  GstElement *sink = gst_element_factory_make ("appsink", nullptr);
  if (!pipeline || !filesrc || !qtdemux || !parse || !dec || !convert || !caps || !sink) {
    printf ("[ERR] element factory failed\n");
    return 1;
  }

  GstCaps *bc = gst_caps_from_string ("video/x-raw(memory:D3D11Memory),format=BGRA");
  g_object_set (caps, "caps", bc, nullptr);
  gst_caps_unref (bc);
  g_object_set (sink, "emit-signals", FALSE, "sync", TRUE, "drop", FALSE,
      "max-buffers", 3, nullptr);
  g_object_set (filesrc, "location", file, nullptr);

  // Inject our device into every D3D11 element (and the bin, for late-children).
  GstContext *devctx = build_d3d11_device_context (gst_device);
  gst_element_set_context (pipeline, devctx);
  gst_element_set_context (dec, devctx);
  gst_element_set_context (convert, devctx);
  gst_context_unref (devctx);

  gst_bin_add_many (GST_BIN (pipeline), filesrc, qtdemux, parse, dec, convert, caps, sink, nullptr);
  GstPad *parse_sink = gst_element_get_static_pad (parse, "sink");
  g_signal_connect (qtdemux, "pad-added", G_CALLBACK (on_demux_pad_added), parse_sink);
  gboolean l1 = gst_element_link (filesrc, qtdemux);
  gboolean l3 = gst_element_link (parse, dec);
  gboolean l4 = gst_element_link (dec, convert);
  gboolean l5 = gst_element_link (convert, caps);
  gboolean l6 = gst_element_link (caps, sink);
  if (!l1 || !l3 || !l4 || !l5 || !l6) {
    printf ("[ERR] link: fs->qtd=%d parse->dec=%d dec->conv=%d conv->caps=%d caps->sink=%d\n",
        l1, l3, l4, l5, l6);
    return 1;
  }

  GstBus *bus = gst_element_get_bus (pipeline);

  // Detect codec and reselect decoder if needed (h264 assumed built above).
  // We try PLAYING; on "no element" style failures the user sees the error.
  printf ("[PIPE] filesrc ! qtdemux ! h264parse ! d3d11h264dec ! d3d11colorconvert ! caps(BGRA,d3d11mem) ! appsink\n");

  if (gst_element_set_state (pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) {
    printf ("[ERR] set PLAYING failed\n");
    return 1;
  }

  ULONGLONG t0 = GetTickCount64 ();
  ULONGLONG last_report = t0;
  unsigned long frames = 0, spout_sends = 0;
  bool error_flag = false, eos_flag = false;
  bool did_seek = false, saw_post_seek = false;
  std::string err_desc;
  int rc = 0;

  while (true) {
    ULONGLONG now = GetTickCount64 ();
    double elapsed = (now - t0) / 1000.0;
    if (seconds > 0 && elapsed > seconds)
      break;
    if (error_flag || eos_flag)
      break;

    if (!did_seek && seek_at > 0 && elapsed >= seek_at) {
      printf ("[SEEK] issuing flush seek to 3.0s at t=%.2f\n", elapsed);
      gst_element_seek_simple (pipeline, GST_FORMAT_TIME,
          (GstSeekFlags) (GST_SEEK_FLAG_FLUSH | GST_SEEK_FLAG_ACCURATE),
          (gint64) (3.0 * GST_SECOND));
      did_seek = true;
    }

    GstSample *sample = gst_app_sink_try_pull_sample (GST_APP_SINK (sink), 100 * GST_MSECOND);
    if (!sample) {
      GstMessage *msg = gst_bus_pop (bus);
      if (msg) { set_message (bus, msg, &error_flag, &err_desc, &eos_flag); gst_message_unref (msg); }
      continue;
    }

    GstBuffer *buf = gst_sample_get_buffer (sample);
    GstMemory *mem = buf ? gst_buffer_peek_memory (buf, 0) : nullptr;
    if (!mem || !gst_is_d3d11_memory (mem)) {
      printf ("[ERR] sample is not D3D11Memory -> CPU readback happened upstream!\n");
      gst_sample_unref (sample);
      rc = 2;
      break;
    }

    GstD3D11Memory *dmem = (GstD3D11Memory *) mem;
    ID3D11Resource *res = gst_d3d11_memory_get_resource_handle (dmem);
    guint sub = gst_d3d11_memory_get_subresource_index (dmem);
    D3D11_TEXTURE2D_DESC td = {};
    ID3D11Texture2D *tex = nullptr;
    bool ok = false;
    if (res) {
      tex = static_cast<ID3D11Texture2D *>(res); // same COM object; QueryInterface below for safety
      ID3D11Texture2D *t2 = nullptr;
      if (SUCCEEDED (res->QueryInterface (__uuidof(ID3D11Texture2D), (void**) &t2)) && t2) {
        t2->GetDesc (&td);
        ID3D11Device *dev_of_tex = nullptr;
        t2->GetDevice (&dev_of_tex);
        bool same = (dev_of_tex == g_device);
        if (dev_of_tex) dev_of_tex->Release ();
        if (frames == 0) {
          printf ("[FRAME0] w=%u h=%u fmt=%u array_size=%u mip=%u sub=%u | device_match=%s\n",
              td.Width, td.Height, td.Format, td.ArraySize, td.MipLevels, sub,
              same ? "YES" : "NO");
        }
        if (same && td.ArraySize == 1 && sub == 0) {
          ok = spout.SendTexture (t2);
          if (ok) spout_sends++;
        } else if (same) {
          printf ("[WARN] texture not directly sendable (array=%u sub=%u)\n", td.ArraySize, sub);
        } else {
          printf ("[ERR] texture device != our device -> NO device coherence\n");
        }
        t2->Release ();
      }
    }
    if (!ok && frames < 3) printf ("[ERR] frame %lu could not be sent (res=%p)\n", frames, (void*) res);

    if (!saw_post_seek && did_seek) {
      guint64 pts = GST_CLOCK_TIME_NONE;
      const GstSegment *seg = gst_sample_get_segment (sample);
      if (buf && GST_BUFFER_PTS (buf) != GST_CLOCK_TIME_NONE) pts = GST_BUFFER_PTS (buf);
      else if (seg && seg->position != (guint64) -1) pts = seg->position;
      printf ("[SEEK] first post-seek frame pts=%.3fs\n", pts / 1e9);
      saw_post_seek = true;
    }
    frames++;

    if (frames == 1) {
      GstMapInfo info;
      if (gst_buffer_map (buf, &info, GST_MAP_READ)) {
        unsigned long long sum = 0;
        for (size_t i = 0; i < info.size; i += 4096) sum += info.data[i];
        gst_buffer_unmap (buf, &info);
        printf ("[DIAG] d3d11 buffer maps for CPU peek (verification only), checksum-ish=%llu\n", sum);
      }
    }

    gst_sample_unref (sample);

    if (now - last_report >= 2000) {
      double fps = frames * 1000.0 / (now - t0);
      printf ("[RUN] t=%.1fs frames=%lu spout_sends=%lu avg_fps=%.1f\n", elapsed, frames, spout_sends, fps);
      last_report = now;
    }

    GstMessage *msg = gst_bus_pop (bus);
    if (msg) { set_message (bus, msg, &error_flag, &err_desc, &eos_flag); gst_message_unref (msg); }
  }

  gst_element_set_state (pipeline, GST_STATE_NULL);
  gst_object_unref (bus);
  gst_object_unref (pipeline);
  gst_object_unref (gst_device);
  spout.ReleaseSender ();
  spout.CloseDirectX11 ();
  g_context->Release ();
  g_device->Release ();
  if (error_flag) rc = 3;
  printf ("[DONE] send frames=%lu spout_sends=%lu rc=%d\n", frames, spout_sends, rc);
  return rc;
}

static void
save_bmp (const char *path, unsigned int w, unsigned int h, const unsigned char *bgra)
{
  FILE *f = nullptr;
  if (fopen_s (&f, path, "wb") != 0 || !f) return;
  BITMAPFILEHEADER bf = {};
  BITMAPINFOHEADER bi = {};
  bf.bfType = 0x4D42;
  bf.bfOffBits = sizeof (BITMAPFILEHEADER) + sizeof (BITMAPINFOHEADER);
  bf.bfSize = bf.bfOffBits + w * h * 3;
  bi.biSize = sizeof (BITMAPINFOHEADER);
  bi.biWidth = (LONG) w;
  bi.biHeight = -(LONG) h;
  bi.biPlanes = 1;
  bi.biBitCount = 24;
  fwrite (&bf, sizeof (bf), 1, f);
  fwrite (&bi, sizeof (bi), 1, f);
  std::vector<unsigned char> row (w * 3);
  for (unsigned int y = 0; y < h; y++) {
    const unsigned char *s = bgra + (size_t) y * w * 4;
    for (unsigned int x = 0; x < w; x++) {
      row[x * 3 + 0] = s[x * 4 + 0];
      row[x * 3 + 1] = s[x * 4 + 1];
      row[x * 3 + 2] = s[x * 4 + 2];
    }
    fwrite (row.data (), 1, row.size (), f);
  }
  fclose (f);
}

static int
do_recv (const char *sender_name, double seconds, const char *bmp_prefix)
{
  WCHAR adapter_name[128] = L"";
  if (!create_d3d_device (adapter_name, 128))
    return 1;

  spoutDX spout;
  if (!spout.OpenDirectX11 (nullptr)) { printf ("[ERR] recv OpenDirectX11\n"); return 1; }
  spout.SetReceiverName (sender_name);

  ULONGLONG t0 = GetTickCount64 ();
  unsigned long got = 0, new_frames = 0;
  unsigned long long last_hash = 0;
  int rc = 1;

  while (true) {
    double elapsed = (GetTickCount64 () - t0) / 1000.0;
    if (elapsed > seconds) break;
    if (spout.ReceiveTexture ()) {
      ID3D11Texture2D *tex = spout.GetSenderTexture ();
      D3D11_TEXTURE2D_DESC desc;
      if (tex) tex->GetDesc (&desc);
      got++;
      if (tex && spout.IsFrameNew ()) {
        new_frames++;
        unsigned char *pixels = new unsigned char[desc.Width * desc.Height * 4];
        if (spout.ReadTexurePixels (tex, pixels)) {
          unsigned long long hash = 0;
          for (size_t i = 0; i < (size_t) desc.Width * desc.Height; i += 997)
            hash = hash * 31 + pixels[i * 4] + (pixels[i * 4 + 1] << 3) + (pixels[i * 4 + 2] << 6);
          if (hash != last_hash && (new_frames % 30 == 1 || new_frames <= 3)) {
            char path[512];
            snprintf (path, sizeof (path), "%s_%05lu.bmp", bmp_prefix, new_frames);
            save_bmp (path, desc.Width, desc.Height, pixels);
            printf ("[RECV] frame %lu size %ux%u fmt=%u hash=%llu saved %s\n",
                new_frames, desc.Width, desc.Height, desc.Format, hash, path);
            last_hash = hash;
            rc = 0;
          }
        }
        delete[] pixels;
      }
    } else {
      Sleep (2);
    }
  }
  printf ("[DONE] recv got=%lu new=%lu rc=%d\n", got, new_frames, rc);
  spout.CloseDirectX11 ();
  g_context->Release ();
  g_device->Release ();
  return rc;
}

int
main (int argc, char **argv)
{
  if (argc < 3) {
    printf ("usage: tcs-gst-proto send <file> <seconds> [sender] [seek_at]\n"
            "       tcs-gst-proto recv [sender] [seconds] [bmp_prefix]\n");
    return 1;
  }
  gst_init (&argc, &argv);
  SetConsoleOutputCP (65001);

  std::string mode = argv[1];
  if (mode == "send") {
    double secs = atof (argv[3]);
    const char *name = argc > 4 ? argv[4] : "TCSGstProto";
    double seek_at = argc > 5 ? atof (argv[5]) : 0.0;
    return do_send (argv[2], secs, name, seek_at);
  } else if (mode == "recv") {
    const char *name = argc > 2 ? argv[2] : "TCSGstProto";
    double secs = argc > 3 ? atof (argv[3]) : 6.0;
    const char *prefix = argc > 4 ? argv[4] : "recv";
    return do_recv (name, secs, prefix);
  }
  printf ("unknown mode\n");
  return 1;
}
