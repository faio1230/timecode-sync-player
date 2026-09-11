/* tcs-shim-test: native smoke test for the tcs_gstreamer C ABI (v3 lease API).
 *
 *   tcs-shim-test <file.mp4> [seconds]
 */
#include "tcs_gstreamer.h"
#include "tcs_delivery_policy.h"
#include <d3d11.h>
#include <psapi.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <thread>
#include <vector>
#include <atomic>



static int failures = 0;
static std::atomic<unsigned> g_frame_notifies{0};
static std::atomic<uint64_t> g_last_gen{0};
static std::atomic<uint64_t> g_last_seq{0};

static void
check (bool cond, const char* what)
{
  printf ("%s: %s\n", cond ? "PASS" : "FAIL", what);
  fflush (stdout);
  if (!cond) failures++;
}

static void
on_frame (void* user, uint64_t generation, uint64_t seq)
{
  (void) user;
  g_frame_notifies++;
  g_last_gen = generation;
  g_last_seq = seq;
}
static size_t
working_set_kb ()
{
  PROCESS_MEMORY_COUNTERS pmc = {};
  if (K32GetProcessMemoryInfo (GetCurrentProcess (), &pmc, sizeof (pmc)))
    return pmc.WorkingSetSize / 1024;
  return 0;
}

/* --stress <file...> <iters>: alternate track switches and watch memory.
 * Exercised path is exactly what the app uses for track switching
 * (tcs_player_load tears the old pipeline down and rebuilds). */
static int
run_stress (int argc, char** argv)
{
  int iters = atoi (argv[argc - 1]);
  int nfiles = argc - 3;
  if (nfiles < 1 || iters < 2) {
    printf ("usage: tcs-shim-test --stress <file...> <iters>\n");
    return 2;
  }

  char err[512] = "";
  TcsPlayer* p = tcs_player_create ("TCSGstShimStress", nullptr, err, sizeof (err));
  check (p != nullptr, "create");
  if (!p) return 1;
  tcs_player_set_frame_callback (p, on_frame, nullptr);

  size_t ws_min = SIZE_MAX, ws_max = 0;
  size_t ws_after_warmup = 0;
  uint64_t frames_total = 0;
  int load_failures = 0;

  for (int i = 0; i < iters; i++) {
    const char* f = argv[2 + (i % nfiles)];
    int rc = tcs_player_load (p, f, -1.0, 0, err, sizeof (err));
    if (rc != TCS_OK) {
      load_failures++;
      printf ("  iter %d: load failed: %s\n", i, err);
      continue;
    }

    uint64_t before;
    TcsStats st = {};
    tcs_player_get_stats (p, &st);
    before = st.frames_decoded;
    for (int w = 0; w < 100; w++) {
      tcs_player_get_stats (p, &st);
      if (st.frames_decoded >= before + 3) break;
      std::this_thread::sleep_for (std::chrono::milliseconds (20));
    }
    if (st.frames_decoded < before + 3) {
      load_failures++;
      printf ("  iter %d: no frames after load (decoder=%s)\n", i, st.decoder);
    }
    frames_total += st.frames_decoded - before;

    /* exercise the lease like the compositor would */
    TcsFrameInfo info = {};
    if (tcs_player_acquire (p, tcs_player_get_generation (p), &info) == 1)
      tcs_player_release (p);

    size_t ws = working_set_kb ();
    if (i == 1) ws_after_warmup = ws;
    if (ws < ws_min) ws_min = ws;
    if (ws > ws_max) ws_max = ws;
    if (i % 5 == 0 || i == iters - 1)
      printf ("  iter %3d ws=%zuKB\n", i, ws);
  }

  long long delta = (long long) ws_max - (long long) (ws_after_warmup ? ws_after_warmup : ws_min);
  printf ("STRESS iters=%d files=%d frames_total=%llu load_failures=%d ws_min=%zuKB ws_max=%zuKB ws_after_warmup=%zuKB delta_after_warmup=%lldKB\n",
      iters, nfiles, (unsigned long long) frames_total, load_failures,
      ws_min, ws_max, ws_after_warmup, delta);

  check (load_failures == 0, "all stress loads produced frames");
  check (frames_total >= (uint64_t) (iters * 3), "frames during stress");
  /* 100MB 繧定ｶ・∴繧句｢怜刈縺ｯ貍上∴縺・・逍代＞ (1080p 謨ｰ譫壹・繝励・繝ｫ蠅怜刈縺ｯ謨ｰ10MB莉･蜀・ */
  bool bounded = ws_after_warmup == 0 || delta < 100 * 1024;
  check (bounded, "working set growth bounded after warmup");

  tcs_player_destroy (p);
  return failures ? 1 : 0;
}


/* Problem H-3: pure delivery policy (no media, no GPU).
 * age_limit = 21 ms in QPC ticks; the tests use 10 MHz ticks for clarity. */
static void
run_delivery_policy_tests ()
{
  TcsDeliveryPlan p;
  const uint64_t limit = 210000; /* 21 ms at 10 MHz */

  p = tcs_delivery_plan (0, 0, limit);
  check (p.lease == 0 && p.drop_oldest == 0, "policy: n=0 -> NotReady");

  p = tcs_delivery_plan (1, 0, limit);
  check (p.lease == 1 && p.drop_oldest == 0, "policy: n=1 -> oldest, no drop");

  p = tcs_delivery_plan (2, 0, limit);
  check (p.lease == 1 && p.drop_oldest == 0, "policy: n=2 fresh -> no drop");

  p = tcs_delivery_plan (2, 1000, limit);
  check (p.lease == 1 && p.drop_oldest == 0, "policy: n=2 1 ms old -> no drop");

  p = tcs_delivery_plan (2, limit - 1, limit);
  check (p.lease == 1 && p.drop_oldest == 0, "policy: n=2 just below 21 ms -> no drop");

  p = tcs_delivery_plan (2, limit, limit);
  check (p.lease == 1 && p.drop_oldest == 1, "policy: n=2 at 21 ms -> drop oldest");

  p = tcs_delivery_plan (2, limit * 3, limit);
  check (p.lease == 1 && p.drop_oldest == 1, "policy: n=2 past 21 ms -> drop oldest");

  p = tcs_delivery_plan (2, 0, 0);
  check (p.lease == 1 && p.drop_oldest == 0, "policy: n=2 with age limit disabled -> no drop");

  p = tcs_delivery_plan (3, 0, limit);
  check (p.lease == 1 && p.drop_oldest == 1, "policy: n=3 -> drop 1 regardless of age");

  p = tcs_delivery_plan (4, 0, limit);
  check (p.lease == 1 && p.drop_oldest == 2, "policy: n=4 -> drop 2");

  p = tcs_delivery_plan (5, 0, limit);
  check (p.lease == 1 && p.drop_oldest == 3, "policy: n=5 -> drop 3");

  p = tcs_delivery_plan (10, 0, limit);
  check (p.lease == 1 && p.drop_oldest == 8, "policy: n=10 -> drop 8 (leave 2)");

  /* Stage 6b: shared ring slot allocation / eviction (pure). */
  uint8_t used[3] = { 0, 0, 0 };
  check (tcs_ring_pick_slot (used, 3) == 0, "ring: all free -> slot 0");
  used[0] = 1;
  check (tcs_ring_pick_slot (used, 3) == 1, "ring: slot0 busy -> slot 1");
  used[1] = 1;
  check (tcs_ring_pick_slot (used, 3) == 2, "ring: slot0,1 busy -> slot 2");
  used[2] = 1;
  check (tcs_ring_pick_slot (used, 3) == -1, "ring: all busy -> evict oldest");
  check (tcs_ring_pick_slot (used, 0) == -1, "ring: zero slots -> -1");
  check (tcs_ring_pick_slot (nullptr, 3) == -1, "ring: null usage -> -1");

  const int32_t no_gpu[3] = { -1, -1, -1 };
  check (tcs_ring_evict_index (no_gpu, 3) == -1, "ring evict: no GPU item -> -1");
  const int32_t mixed[3] = { -1, 1, 2 };
  check (tcs_ring_evict_index (mixed, 3) == 1, "ring evict: oldest GPU item index");
  const int32_t single[1] = { 0 };
  check (tcs_ring_evict_index (single, 1) == 0, "ring evict: first item");
  check (tcs_ring_evict_index (single, 0) == -1, "ring evict: empty -> -1");
}

int
main (int argc, char** argv)
{
  if (argc < 2) { printf ("usage: tcs-shim-test <media> [play_secs]\n"); return 2; }
  setvbuf (stdout, nullptr, _IONBF, 0);
  if (strcmp (argv[1], "--policy-only") == 0) {
    run_delivery_policy_tests ();
    printf ("RESULT failures=%d\n", failures);
    return failures == 0 ? 0 : 1;
  }
  run_delivery_policy_tests ();
  if (strcmp (argv[1], "--stress") == 0)
    return run_stress (argc, argv);
  const char* file = argv[1];
  double play_secs = argc > 2 ? atof (argv[2]) : 2.0;

  char err[512] = "";
  TcsPlayer* p = tcs_player_create ("TCSGstShimTest", nullptr, err, sizeof (err));
  check (p != nullptr, "create (internal device)");
  if (!p) { printf ("  err=%s\n", err); return 1; }

  tcs_player_set_frame_callback (p, on_frame, nullptr);

  int rc = tcs_player_load (p, file, -1.0, 1, err, sizeof (err));
  check (rc == TCS_OK, "load paused");
  if (rc != TCS_OK) printf ("  err=%s\n", err);

  TcsStats st = {};
  tcs_player_get_stats (p, &st);
  printf ("  decoder=%s gpu_path=%d size=%dx%d gen=%llu\n",
      st.decoder, st.gpu_path, st.width, st.height, (unsigned long long) st.generation);
  check (st.width > 0 && st.height > 0, "size>0");

  /* lease API: acquire at current generation */
  uint64_t gen = tcs_player_get_generation (p);
  TcsFrameInfo info = {};
  int got = tcs_player_acquire (p, gen, &info);
  check (got == 1, "acquire current-gen frame");
  check (info.is_gpu == 1, "leased frame is GPU (D3D11Memory)");
  check (info.generation == gen, "lease generation matches");
  check (info.slot >= 0 && info.slot < 3, "lease comes from the shared ring (slot 0..2)");

  /* Stage 6b: the ring must be exposed with 3 NT handles + a shared fence. */
  {
    void* handles[3] = { nullptr, nullptr, nullptr };
    void* ring_fence = nullptr;
    uint32_t count = 0, rw = 0, rh = 0;
    int rrc = tcs_player_ring_info (p, handles, 3, &count, &ring_fence, &rw, &rh);
    check (rrc == TCS_OK && count == 3 && ring_fence != nullptr,
        "ring_info returns 3 handles + fence");
    check (handles[0] && handles[1] && handles[2], "ring handles non-null");
    check (rw == (uint32_t) st.width && rh == (uint32_t) st.height, "ring size matches media");
    void* one[1] = { nullptr };
    check (tcs_player_ring_info (p, one, 1, &count, &ring_fence, &rw, &rh) == TCS_ERR_SIZE,
        "ring_info capacity too small -> TCS_ERR_SIZE");
  }

  void* tex = nullptr; uint32_t sub = 0, fmt = 0;
  rc = tcs_player_leased_texture (p, &tex, &sub, &fmt);
  check (rc == TCS_OK && tex != nullptr, "leased texture pointer");
  check (fmt == 87 || fmt == 88, "dxgi format is BGRA(87/88)"); /* 87=B8G8R8A8 */

  /* old generation must be refused (none), current lease stays */
  int got_old = tcs_player_acquire (p, gen - 1, &info);
  check (got_old == 0, "stale generation refused (none)");

  /* cpu copy while leased (preview path) */
  std::vector<uint8_t> pixels ((size_t) st.width * st.height * 4, 0);
  rc = tcs_player_leased_cpu_copy (p, pixels.data (), st.width * 4);
  check (rc == TCS_OK, "leased cpu copy");
  unsigned long long sum = 0;
  for (size_t i = 0; i < pixels.size (); i += 4099) sum += pixels[i];
  check (sum > 0, "cpu copy non-black");

  /* second acquire while leased returns the same lease */
  TcsFrameInfo info2 = {};
  check (tcs_player_acquire (p, gen, &info2) == 1 && info2.seq == info.seq,
      "acquire while leased = same lease");
  tcs_player_release (p);

  /* playback advances leases */
  tcs_player_set_paused (p, 0);
  uint64_t f1 = st.frames_decoded;
  unsigned notifs1 = g_frame_notifies.load ();
  for (int s = 0; s < (int) play_secs; s++) {
    std::this_thread::sleep_for (std::chrono::seconds (1));
    TcsStats ps = {};
    tcs_player_get_stats (p, &ps);
    printf ("  t=%ds frames=%llu notifies=%u\n", s + 1,
        (unsigned long long) ps.frames_decoded, g_frame_notifies.load ());
  }
  tcs_player_get_stats (p, &st);
  check (st.frames_decoded > f1 + 5, "frames advance while playing");
  check (g_frame_notifies.load () > notifs1, "frame callback notifications");

  double pos = 0;
  check (tcs_player_get_time_pos (p, &pos) == TCS_OK && pos > 0.05, "time-pos advances");
  (void) pos;

  /* verification-layer Spout publish from GPU texture */
  gen = tcs_player_get_generation (p);
  if (tcs_player_acquire (p, gen, &info) == 1)
    check (tcs_player_publish_spout (p) == TCS_OK, "publish_spout (verification)");
  tcs_player_release (p);

  /* seek bumps generation; a stale lease must be released by the owner first */
  double dur = 0;
  tcs_player_get_duration (p, &dur);
  double target = dur > 2.0 ? dur / 2.0 : 0.5;
  tcs_player_release (p);
  uint64_t gen2 = tcs_player_seek (p, target);
  check (gen2 == gen + 1, "seek bumps generation");
  TcsFrameInfo stale = {};
  check (tcs_player_acquire (p, gen, &stale) == 0, "old-gen acquire = none after seek");

  std::this_thread::sleep_for (std::chrono::milliseconds (250));
  gen = gen2;
  got = 0;
  for (int i = 0; i < 20 && !got; i++) {
    got = tcs_player_acquire (p, gen, &info);
    if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (50));
  }
  check (got == 1, "new-gen frame available after seek");
  if (got) {
    double pts_s = info.pts_ns / 1e9;
    printf ("  post-seek lease pts=%.3f target=%.3f seq=%llu\n", pts_s, target,
        (unsigned long long) info.seq);
    check (pts_s > target - 0.6 && pts_s < target + 1.6, "lease pts near seek target");
  }
  tcs_player_release (p);

  /* pause + step: generation bump per step */
  tcs_player_set_paused (p, 1);
  std::this_thread::sleep_for (std::chrono::milliseconds (100));
  uint64_t gen3 = tcs_player_step_frame (p);
  check (gen3 == gen2 + 1, "step bumps generation");
  std::this_thread::sleep_for (std::chrono::milliseconds (150));
  got = tcs_player_acquire (p, gen3, &info);
  check (got == 1, "stepped frame leased");
  tcs_player_release (p);

  /* reload (track switch) -> generation bump, new frames */
  rc = tcs_player_load (p, file, 0.25, 0, err, sizeof (err));
  check (rc == TCS_OK, "reload (track switch)");
  std::this_thread::sleep_for (std::chrono::milliseconds (300));
  tcs_player_get_stats (p, &st);
  check (st.frames_decoded > 0, "frames after reload");
  check (st.gpu_path == 1, "gpu path after reload");
  tcs_player_release (p);

  /* stop: lease slot and latest cleared */
  rc = tcs_player_stop (p);
  check (rc == TCS_OK, "stop");
  gen = tcs_player_get_generation (p);
  check (tcs_player_acquire (p, gen, &info) == 0, "no lease after stop");
  check (tcs_player_get_time_pos (p, &pos) != TCS_OK, "time-pos invalid after stop");

  /* external-device mode: caller-owned device shared with compositor.
   * Verifies command 2 (device injection) and that leased textures live on
   * the caller's device (no cross-device sharing needed). */
  {
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    HRESULT hr = D3D11CreateDevice (nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &dev, nullptr, &ctx);
    check (SUCCEEDED (hr), "external device created");
    if (SUCCEEDED (hr)) {
      TcsPlayer* p2 = tcs_player_create ("TCSGstShimTest", dev, err, sizeof (err));
      check (p2 != nullptr, "create (external device)");
      if (p2) {
        int r2 = tcs_player_load (p2, file, -1.0, 0, err, sizeof (err));
        check (r2 == TCS_OK, "load on external device");
        std::this_thread::sleep_for (std::chrono::milliseconds (300));
        uint64_t g2 = tcs_player_get_generation (p2);
        TcsFrameInfo i2 = {};
        if (tcs_player_acquire (p2, g2, &i2) == 1) {
          /* Stage 6b: the shim creates its OWN device on the caller's adapter
           * LUID; the lease never lives on the caller device. */
          check (i2.slot >= 0 && i2.slot < 3, "external-LUID lease uses the ring slot");
          void* tex2 = nullptr; uint32_t s2 = 0, f2 = 0;
          r2 = tcs_player_leased_texture (p2, &tex2, &s2, &f2);
          if (r2 == TCS_OK && tex2) {
            ID3D11Device* tdev = nullptr;
            ((ID3D11Texture2D*) tex2)->GetDevice (&tdev);
            check (tdev != dev, "leased texture is on a separate shim device");
            bool same_adapter = false;
            if (tdev) {
              IDXGIDevice* tdxgi = nullptr;
              if (SUCCEEDED (tdev->QueryInterface (__uuidof(IDXGIDevice), (void**) &tdxgi)) && tdxgi) {
                IDXGIAdapter* ta = nullptr;
                if (SUCCEEDED (tdxgi->GetAdapter (&ta)) && ta) {
                  DXGI_ADAPTER_DESC td = {};
                  ta->GetDesc (&td);
                  IDXGIDevice* cdxgi = nullptr;
                  if (SUCCEEDED (dev->QueryInterface (__uuidof(IDXGIDevice), (void**) &cdxgi)) && cdxgi) {
                    IDXGIAdapter* ca = nullptr;
                    if (SUCCEEDED (cdxgi->GetAdapter (&ca)) && ca) {
                      DXGI_ADAPTER_DESC cd = {};
                      ca->GetDesc (&cd);
                      same_adapter = memcmp (&td.AdapterLuid, &cd.AdapterLuid, sizeof (LUID)) == 0;
                      ca->Release ();
                    }
                    cdxgi->Release ();
                  }
                  ta->Release ();
                }
                tdxgi->Release ();
              }
              tdev->Release ();
            }
            check (same_adapter, "shim device uses the caller adapter LUID");
          } else {
            check (false, "leased texture on shim device");
          }
          tcs_player_release (p2);
        } else {
          check (false, "acquire on external device");
        }
        tcs_player_destroy (p2);
      }
      if (ctx) ctx->Release ();
      if (dev) dev->Release ();
    }
  }

  tcs_player_destroy (p);
  printf ("RESULT failures=%d\n", failures);
  return failures ? 1 : 0;
}
