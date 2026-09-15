/* tcs-shim-test: native smoke test for the tcs_gstreamer C ABI (v3 lease API).
 *
 *   tcs-shim-test <file.mp4> [seconds]
 */
#include "tcs_gstreamer.h"
#include "tcs_delivery_policy.h"
#include "tcs_decode_policy.h"
#include "tcs_video_profiles.h"
#include <d3d11.h>
#include <psapi.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <thread>
#include <vector>
#include <atomic>
#include <algorithm>



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


/* --load-bench <file...>: time tcs_player_load for track switching (S4).
 * Loads each file in round-robin order, mirroring the app's next/prev path
 * (tcs_player_load tears the old pipeline down and rebuilds). The shim's own
 * [tcs-gst] load.attempt lines carry the per-phase breakdown; this mode adds
 * the caller-visible wall time and a min/median/max summary. */
static int
run_load_bench (int argc, char** argv)
{
  if (argc < 3) {
    printf ("usage: tcs-shim-test --load-bench <file...> [iters] [paused]\n");
    return 2;
  }
  int iters = 1;
  int paused = 0;
  int nfiles = argc - 2;
  if (nfiles > 1 && atoi (argv[argc - 1]) > 0) {
    iters = atoi (argv[argc - 1]);
    nfiles--;
  }
  if (nfiles > 1 && (strcmp (argv[argc - 1], "paused") == 0)) {
    paused = 1;
    nfiles--;
  }
  if (nfiles < 1) {
    printf ("usage: tcs-shim-test --load-bench <file...> [iters] [paused]\n");
    return 2;
  }

  char err[512] = "";
  TcsPlayer* p = tcs_player_create ("TCSGstShimLoadBench", nullptr, err, sizeof (err));
  check (p != nullptr, "create (internal device)");
  if (!p) { printf ("  err=%s\n", err); return 1; }

  std::vector<double> wall_ms;
  int load_failures = 0;
  for (int round = 0; round < iters; round++) {
    for (int f = 0; f < nfiles; f++) {
      const char* file = argv[2 + f];
      tcs_player_release (p);
      auto t0 = std::chrono::steady_clock::now ();
      int rc = tcs_player_load (p, file, -1.0, paused, err, sizeof (err));
      double ms = std::chrono::duration<double, std::milli> (
          std::chrono::steady_clock::now () - t0).count ();
      TcsStats st = {};
      tcs_player_get_stats (p, &st);
      if (rc != TCS_OK) {
        load_failures++;
        printf ("LOAD round=%d file=%s result=FAIL err=%s\n", round, file, err);
        fflush (stdout);
        continue;
      }
      wall_ms.push_back (ms);
      printf ("LOAD round=%d file=%s paused=%d wall_ms=%.1f frames=%llu decoder=%s\n",
          round, file, paused, ms, (unsigned long long) st.frames_decoded, st.decoder);
      fflush (stdout);
    }
  }
  if (!wall_ms.empty ()) {
    std::vector<double> sorted = wall_ms;
    std::sort (sorted.begin (), sorted.end ());
    double sum = 0;
    for (double v : sorted) sum += v;
    double median = sorted[sorted.size () / 2];
    printf ("LOAD-BENCH loads=%zu failures=%d min=%.1f median=%.1f mean=%.1f max=%.1f\n",
        sorted.size (), load_failures, sorted.front (), median,
        sum / (double) sorted.size (), sorted.back ());
  }
  check (load_failures == 0, "all bench loads succeeded");
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

  /* V11: decode profile order (pure). 4 GPU profiles then 5 CPU profiles,
   * mirroring the shim's table shape (9 profiles + decodebin fallback). */
  {
    const int software_flags[9] = { 0, 0, 0, 0, 1, 1, 1, 1, 1 };
    int order[10];
    int n;

    /* hardware, no cached profile: table order, then decodebin. */
    n = tcs_decode_profile_order (0, 0, software_flags, 9, order);
    check (n == 10, "decode order: hardware count");
    check (order[0] == 0 && order[1] == 1 && order[2] == 2 && order[3] == 3 &&
           order[4] == 4 && order[5] == 5 && order[6] == 6 && order[7] == 7 &&
           order[8] == 8 && order[9] == TCS_DECODE_PROFILE_FALLBACK,
        "decode order: hardware is table order + fallback");

    /* hardware, cached CPU profile: cache first, fallback last. */
    n = tcs_decode_profile_order (0, 6, software_flags, 9, order);
    check (n == 10 && order[0] == 6 && order[9] == TCS_DECODE_PROFILE_FALLBACK,
        "decode order: hardware keeps last-good first");

    /* software: CPU profiles, decodebin, then GPU profiles. */
    n = tcs_decode_profile_order (1, 0, software_flags, 9, order);
    check (n == 10, "decode order: software count");
    check (order[0] == 4 && order[1] == 5 && order[2] == 6 && order[3] == 7 &&
           order[4] == 8 && order[5] == TCS_DECODE_PROFILE_FALLBACK &&
           order[6] == 0 && order[7] == 1 && order[8] == 2 && order[9] == 3,
        "decode order: software is CPU + decodebin + GPU");

    /* software, cached CPU profile: cache first, no duplicate. */
    n = tcs_decode_profile_order (1, 5, software_flags, 9, order);
    check (n == 10 && order[0] == 5 && order[1] == 4 && order[2] == 6 &&
           order[5] == TCS_DECODE_PROFILE_FALLBACK && order[6] == 0,
        "decode order: software keeps a CPU last-good first");

    /* software, cached GPU profile: the GPU cache stays in the GPU block. */
    n = tcs_decode_profile_order (1, 2, software_flags, 9, order);
    check (n == 10 && order[0] == 4 && order[5] == TCS_DECODE_PROFILE_FALLBACK &&
           order[6] == 0 && order[7] == 1 && order[8] == 2 && order[9] == 3,
        "decode order: software does not promote a GPU last-good");
  }

  /* V11-h: pin the CPU/GPU classification of the real profile table. The chain
   * builder derives it from `conv` ("d3d11" means GPU), so a CPU profile whose
   * conv became a d3d11 converter would lose d3d11upload (and the software
   * search order). CPU conversion for V11-h is a separate post-upload element. */
  {
    check (TCS_VIDEO_PROFILE_COUNT == 9, "profiles: table has 9 entries");
    int flags_ok = 1;
    for (int i = 0; i < TCS_VIDEO_PROFILE_COUNT; i++)
      flags_ok = flags_ok && (tcs_video_profile_is_software (i) == (i >= 4 ? 1 : 0));
    check (flags_ok == 1, "profiles: software flags are the CPU profiles only");
    for (int i = 0; i < 4; i++)
      check (strstr (kTcsVideoProfiles[i].conv, "d3d11") != nullptr,
          "profiles: GPU profiles convert on the GPU");
    for (int i = 4; i < TCS_VIDEO_PROFILE_COUNT; i++)
      check (strstr (kTcsVideoProfiles[i].conv, "d3d11") == nullptr,
          "profiles: CPU profiles keep a CPU converter (upload must be built)");
  }
}

/* --seek-loop <file> [iters]: consecutive seeks (V5). Every target must land
 * within one frame and within the 500ms budget, and the last three seeks must
 * not be worse than the first three (no accumulated correction). */
static int
run_seek_loop (int argc, char** argv)
{
  if (argc < 3) {
    printf ("usage: tcs-shim-test --seek-loop <file> [iters]\n");
    return 2;
  }
  const char* file = argv[2];
  int iters = argc > 3 ? atoi (argv[3]) : 10;
  if (iters < 2)
    iters = 2;

  char err[512] = "";
  TcsPlayer* p = tcs_player_create ("TCSGstShimSeekLoop", nullptr, err, sizeof (err));
  check (p != nullptr, "create (internal device)");
  if (!p) return 1;
  tcs_player_set_frame_callback (p, on_frame, nullptr);
  int rc = tcs_player_load (p, file, -1.0, 0, err, sizeof (err));
  check (rc == TCS_OK, "load playing");
  if (rc != TCS_OK) { printf ("  err=%s\n", err); tcs_player_destroy (p); return 1; }
  {
    TcsStats st = {};
    for (int w = 0; w < 100; w++) {
      tcs_player_get_stats (p, &st);
      if (st.frames_decoded >= 3) break;
      std::this_thread::sleep_for (std::chrono::milliseconds (20));
    }
  }
  double dur = 0, fps = 0;
  tcs_player_get_duration (p, &dur);
  tcs_player_get_fps (p, &fps);
  double frame_s = fps > 0.0 ? 1.0 / fps : 0.040;
  printf ("  media duration=%.3fs fps=%.3f frame=%.1fms\n", dur, fps, frame_s * 1000.0);

  std::vector<double> arrivals, deltas;
  int bad_landing = 0, slow = 0, no_frame = 0;
  for (int i = 0; i < iters; i++) {
    double target = dur > 2.0
        ? dur * (0.08 + 0.84 * ((double) ((i * 37) % 100) / 99.0))
        : 0.5;
    tcs_player_release (p);
    auto t0 = std::chrono::steady_clock::now ();
    uint64_t gen = tcs_player_seek (p, target);
    TcsFrameInfo info = {};
    int got = 0;
    for (int k = 0; k < 500 && !got; k++) {
      got = tcs_player_acquire (p, gen, &info);
      if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (2));
    }
    double ms = std::chrono::duration<double, std::milli> (
        std::chrono::steady_clock::now () - t0).count ();
    if (!got) {
      no_frame++;
      printf ("  seek %2d target=%.3f NO FRAME (%.1fms)\n", i, target, ms);
      continue;
    }
    double pts = info.pts_ns / 1e9;
    double delta_ms = (pts - target) * 1000.0;
    arrivals.push_back (ms);
    deltas.push_back (delta_ms);
    if (pts < target - 0.001 || pts > target + frame_s + 0.001)
      bad_landing++;
    if (ms >= 500.0)
      slow++;
    printf ("  seek %2d target=%.3f lease=%.3f delta=%+.1fms arrival=%.1fms\n",
        i, target, pts, delta_ms, ms);
    tcs_player_release (p);
    std::this_thread::sleep_for (std::chrono::milliseconds (50));
  }
  tcs_player_destroy (p);

  check (no_frame == 0, "every consecutive seek produced a frame");
  check (bad_landing == 0, "every consecutive seek landed within one frame");
  check (slow == 0, "every consecutive seek arrived within 500ms");
  if (arrivals.size () >= 6) {
    double first = 0, last = 0;
    for (size_t i = 0; i < 3; i++) {
      first += arrivals[i];
      last += arrivals[arrivals.size () - 3 + i];
    }
    printf ("  arrival first3=%.1fms last3=%.1fms\n", first / 3.0, last / 3.0);
    check (last / 3.0 <= (first / 3.0) * 1.5 + 50.0,
        "arrival does not degrade over consecutive seeks");
  }
  return failures;
}

/* --paused-seek <file> [target]: D2 reproduction. Load playing, seek while
 * playing (control), pause, seek again and measure how long the new-position
 * frame takes; if it never arrives, resume and measure when it does. Then a
 * frame-step for contrast (the step path already handles the paused case by
 * briefly going PLAYING). */
static int
run_paused_seek (int argc, char** argv)
{
  if (argc < 3) {
    printf ("usage: tcs-shim-test --paused-seek <file> [target]\n");
    return 2;
  }
  const char* file = argv[2];

  char err[512] = "";
  TcsPlayer* p = tcs_player_create ("TCSGstShimPausedSeek", nullptr, err, sizeof (err));
  check (p != nullptr, "create (internal device)");
  if (!p) { printf ("  err=%s\n", err); return 1; }
  tcs_player_set_frame_callback (p, on_frame, nullptr);

  int rc = tcs_player_load (p, file, -1.0, 0, err, sizeof (err));
  check (rc == TCS_OK, "load playing");
  if (rc != TCS_OK) { printf ("  err=%s\n", err); tcs_player_destroy (p); return 1; }
  for (int w = 0; w < 200; w++) {
    TcsStats st = {};
    tcs_player_get_stats (p, &st);
    if (st.frames_decoded >= 3) break;
    std::this_thread::sleep_for (std::chrono::milliseconds (10));
  }

  double dur = 0, fps = 0;
  tcs_player_get_duration (p, &dur);
  tcs_player_get_fps (p, &fps);
  double target_play = dur > 2.0 ? dur * 0.45 : 0.5;
  double target_paused = dur > 2.0 ? dur * 0.65 : 0.7;
  printf ("  media duration=%.3fs fps=%.3f play-target=%.3f paused-target=%.3f\n",
      dur, fps, target_play, target_paused);

  /* control: seek while PLAYING */
  tcs_player_release (p);
  auto t0 = std::chrono::steady_clock::now ();
  uint64_t gen = tcs_player_seek (p, target_play);
  double play_call_ms = std::chrono::duration<double, std::milli> (
      std::chrono::steady_clock::now () - t0).count ();
  TcsFrameInfo info = {};
  int got = 0;
  for (int i = 0; i < 1500 && !got; i++) {
    got = tcs_player_acquire (p, gen, &info);
    if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (2));
  }
  double play_ms = std::chrono::duration<double, std::milli> (
      std::chrono::steady_clock::now () - t0).count ();
  printf ("  PLAYING seek target=%.3f call=%.2fms got=%d arrival=%.1fms pts=%.3f\n",
      target_play, play_call_ms, got, play_ms, got ? info.pts_ns / 1e9 : -1.0);
  check (got == 1, "playing seek produced a frame");
  check (play_ms < 500.0, "playing seek arrival within 500ms");
  tcs_player_release (p);
  std::this_thread::sleep_for (std::chrono::milliseconds (100));

  /* paused seek */
  tcs_player_set_paused (p, 1);
  std::this_thread::sleep_for (std::chrono::milliseconds (300));
  tcs_player_release (p);
  t0 = std::chrono::steady_clock::now ();
  uint64_t gen2 = tcs_player_seek (p, target_paused);
  double paused_call_ms = std::chrono::duration<double, std::milli> (
      std::chrono::steady_clock::now () - t0).count ();
  got = 0;
  for (int i = 0; i < 1500 && !got; i++) {
    got = tcs_player_acquire (p, gen2, &info);
    if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (2));
  }
  double paused_ms = std::chrono::duration<double, std::milli> (
      std::chrono::steady_clock::now () - t0).count ();
  if (got) {
    printf ("  PAUSED seek target=%.3f call=%.2fms got=1 arrival=%.1fms pts=%.3f\n",
        target_paused, paused_call_ms, paused_ms, info.pts_ns / 1e9);
  } else {
    printf ("  PAUSED seek target=%.3f call=%.2fms NO FRAME in %.1fms\n",
        target_paused, paused_call_ms, paused_ms);
  }
  /* The deterministic test hook deliberately holds frame_lock inside the
   * prepare phase, so the wall time of the call is not a product measurement
   * while it is set. */
  const char* seek_hold = getenv ("TCS_TEST_HOLD_SEEK_LOCK_MS");
  bool seek_hold_on = seek_hold != nullptr && atoi (seek_hold) > 0;
  check (seek_hold_on || paused_call_ms < 20.0, "paused seek returns without blocking");
  check (got == 1 && paused_ms < 250.0, "paused seek arrival within 250ms");

  /* contrast: the same paused pipeline delivers the frame once PLAYING runs,
   * which is exactly what step_frame already does for one frame. */
  if (!got) {
    tcs_player_release (p);
    auto tr = std::chrono::steady_clock::now ();
    tcs_player_set_paused (p, 0);
    for (int i = 0; i < 1500 && !got; i++) {
      got = tcs_player_acquire (p, gen2, &info);
      if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (2));
    }
    double resume_ms = std::chrono::duration<double, std::milli> (
        std::chrono::steady_clock::now () - tr).count ();
    printf ("  PAUSED seek frame after resume: got=%d arrival-after-resume=%.1fms\n",
        got, resume_ms);
    check (got == 1, "resume delivered the pending paused-seek frame");
    tcs_player_release (p);
    tcs_player_set_paused (p, 1);
    std::this_thread::sleep_for (std::chrono::milliseconds (300));
  }

  /* race: resume immediately after arming the pump (resume must win) */
  {
    tcs_player_set_paused (p, 1);
    std::this_thread::sleep_for (std::chrono::milliseconds (200));
    tcs_player_release (p);
    double race_target = dur > 2.0 ? dur * 0.30 : 0.3;
    uint64_t gen4 = tcs_player_seek (p, race_target);
    tcs_player_set_paused (p, 0);
    got = 0;
    for (int i = 0; i < 250 && !got; i++) {
      got = tcs_player_acquire (p, gen4, &info);
      if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (2));
    }
    printf ("  RESUME during pump: got=%d\n", got);
    check (got == 1, "resume during pump still delivers the seek frame");
    TcsStats s1 = {}, s2 = {};
    tcs_player_get_stats (p, &s1);
    std::this_thread::sleep_for (std::chrono::milliseconds (200));
    tcs_player_get_stats (p, &s2);
    printf ("  RESUME during pump: frames %llu -> %llu\n",
        (unsigned long long) s1.frames_decoded, (unsigned long long) s2.frames_decoded);
    check (s2.frames_decoded > s1.frames_decoded, "resume during pump keeps playing");
    tcs_player_release (p);
  }

  /* race: back-to-back paused seeks re-arm the pump */
  {
    tcs_player_set_paused (p, 1);
    std::this_thread::sleep_for (std::chrono::milliseconds (200));
    tcs_player_release (p);
    double t_a = dur > 2.0 ? dur * 0.20 : 0.2;
    double t_b = dur > 2.0 ? dur * 0.80 : 0.8;
    tcs_player_seek (p, t_a);
    auto tb0 = std::chrono::steady_clock::now ();
    uint64_t gen5 = tcs_player_seek (p, t_b);
    got = 0;
    for (int i = 0; i < 1500 && !got; i++) {
      got = tcs_player_acquire (p, gen5, &info);
      if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (2));
    }
    double back_ms = std::chrono::duration<double, std::milli> (
        std::chrono::steady_clock::now () - tb0).count ();
    printf ("  BACK-TO-BACK paused seeks: got=%d second-arrival=%.1fms pts=%.3f\n",
        got, back_ms, got ? info.pts_ns / 1e9 : -1.0);
    check (got == 1 && back_ms < 250.0, "second paused seek still arrives within 250ms");
    tcs_player_release (p);
  }

  /* contrast: step_frame's temporary PLAYING path */
  {
    tcs_player_set_paused (p, 1);
    std::this_thread::sleep_for (std::chrono::milliseconds (200));
    tcs_player_release (p);
    auto ts = std::chrono::steady_clock::now ();
    uint64_t gen3 = tcs_player_step_frame (p);
    got = 0;
    for (int i = 0; i < 500 && !got; i++) {
      got = tcs_player_acquire (p, gen3, &info);
      if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (2));
    }
    double step_ms = std::chrono::duration<double, std::milli> (
        std::chrono::steady_clock::now () - ts).count ();
    printf ("  STEP (temporary PLAYING) got=%d arrival=%.1fms pts=%.3f\n",
        got, step_ms, got ? info.pts_ns / 1e9 : -1.0);
    check (got == 1 && step_ms < 500.0, "step frame arrival within 500ms");
    tcs_player_release (p);
  }

  tcs_player_destroy (p);
  return failures ? 1 : 0;
}

/* --seek-method-check <file> [seeks]: C1(b) verification. The process runs
 * with TCS_SEEK_METHOD from the environment (the shim reads it once at load),
 * so this mode is executed once per value. Every seek must produce a frame
 * that lands on the first frame at/after the target (the keyunit gate drops
 * the pre-target snap frames; accurate lands within one frame). */
static int
run_seek_method_check (int argc, char** argv)
{
  if (argc < 3) {
    printf ("usage: tcs-shim-test --seek-method-check <file> [seeks]\n");
    return 2;
  }
  const char* file = argv[2];
  int seeks = argc > 3 ? atoi (argv[3]) : 5;
  if (seeks < 1)
    seeks = 1;
  const char* method_env = getenv ("TCS_SEEK_METHOD");
  printf ("  TCS_SEEK_METHOD=%s\n", method_env ? method_env : "(unset)");

  char err[512] = "";
  TcsPlayer* p = tcs_player_create ("TCSGstShimSeekMethod", nullptr, err, sizeof (err));
  check (p != nullptr, "create (internal device)");
  if (!p) { printf ("  err=%s\n", err); return 1; }
  tcs_player_set_frame_callback (p, on_frame, nullptr);
  int rc = tcs_player_load (p, file, -1.0, 0, err, sizeof (err));
  check (rc == TCS_OK, "load playing");
  if (rc != TCS_OK) { printf ("  err=%s\n", err); tcs_player_destroy (p); return 1; }
  {
    TcsStats st = {};
    for (int w = 0; w < 200; w++) {
      tcs_player_get_stats (p, &st);
      if (st.frames_decoded >= 3) break;
      std::this_thread::sleep_for (std::chrono::milliseconds (10));
    }
  }
  double dur = 0, fps = 0;
  tcs_player_get_duration (p, &dur);
  tcs_player_get_fps (p, &fps);
  double frame_s = fps > 0.0 ? 1.0 / fps : 0.04;

  int no_frame = 0, before_target = 0, slow = 0;
  for (int i = 0; i < seeks; i++) {
    double target = dur > 2.0
        ? dur * (0.12 + 0.76 * ((double) ((i * 41) % 100) / 99.0))
        : 0.5;
    tcs_player_release (p);
    auto t0 = std::chrono::steady_clock::now ();
    uint64_t gen = tcs_player_seek (p, target);
    TcsFrameInfo info = {};
    int got = 0;
    for (int k = 0; k < 2500 && !got; k++) {
      got = tcs_player_acquire (p, gen, &info);
      if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (2));
    }
    double ms = std::chrono::duration<double, std::milli> (
        std::chrono::steady_clock::now () - t0).count ();
    if (!got) {
      no_frame++;
      printf ("  seek %2d target=%.3f NO FRAME (%.1fms)\n", i, target, ms);
      continue;
    }
    double pts = info.pts_ns / 1e9;
    double delta_ms = (pts - target) * 1000.0;
    /* The invariant every method must keep: never hand over a frame from
     * before the target. The exact landing distance is method/container
     * specific and is printed for the comparison (frame_s=%.1fms). */
    bool at_or_after = pts >= target - 0.001;
    if (!at_or_after)
      before_target++;
    if (ms >= 5000.0)
      slow++;
    printf ("  seek %2d target=%.3f lease=%.3f delta=%+.1fms arrival=%.1fms at_or_after=%d\n",
        i, target, pts, delta_ms, ms, at_or_after ? 1 : 0);
    tcs_player_release (p);
    std::this_thread::sleep_for (std::chrono::milliseconds (50));
  }
  tcs_player_destroy (p);

  check (no_frame == 0, "every seek produced a frame");
  check (before_target == 0, "no seek handed over a frame before the target");
  check (slow == 0, "every seek arrived within 5000ms");
  printf ("  (frame period is %.1fms; delta is informational)\n", frame_s * 1000.0);
  return failures;
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
  if (strcmp (argv[1], "--load-bench") == 0) {
    int rc = run_load_bench (argc, argv);
    printf ("RESULT failures=%d\n", failures);
    return failures == 0 ? 0 : 1;
  }
  if (strcmp (argv[1], "--seek-loop") == 0) {
    int rc = run_seek_loop (argc, argv);
    printf ("RESULT failures=%d\n", failures);
    return failures == 0 ? 0 : 1;
  }
  if (strcmp (argv[1], "--paused-seek") == 0) {
    run_paused_seek (argc, argv);
    printf ("RESULT failures=%d\n", failures);
    return failures == 0 ? 0 : 1;
  }
  if (strcmp (argv[1], "--seek-method-check") == 0) {
    run_seek_method_check (argc, argv);
    printf ("RESULT failures=%d\n", failures);
    return failures == 0 ? 0 : 1;
  }
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
  auto play_t0 = std::chrono::steady_clock::now ();
  for (int s = 0; s < (int) play_secs; s++) {
    std::this_thread::sleep_for (std::chrono::seconds (1));
    TcsStats ps = {};
    tcs_player_get_stats (p, &ps);
    printf ("  t=%ds frames=%llu notifies=%u\n", s + 1,
        (unsigned long long) ps.frames_decoded, g_frame_notifies.load ());
  }
  auto play_t1 = std::chrono::steady_clock::now ();
  tcs_player_get_stats (p, &st);
  check (st.frames_decoded > f1 + 5, "frames advance while playing");
  check (g_frame_notifies.load () > notifs1, "frame callback notifications");
  {
    double secs = std::chrono::duration<double> (play_t1 - play_t0).count ();
    double rate = secs > 0.0 ? (double) (st.frames_decoded - f1) / secs : 0.0;
    double media_fps = 0.0;
    tcs_player_get_fps (p, &media_fps);
    printf ("  playback-rate=%.2f fps media-fps=%.2f\n", rate, media_fps);
    /* The rate count can spike when the source carries duplicate PTS frames
     * (some remuxed TS files do); verify the *pacing* with the delivery trace
     * instead: the median inter-arrival must match the media frame period. */
    LARGE_INTEGER qfreq;
    QueryPerformanceFrequency (&qfreq);
    std::vector<TcsDeliveryEvent> evs (4096);
    uint32_t n = 0;
    tcs_player_drain_delivery_events (p, evs.data (), (uint32_t) evs.size (), &n);
    std::vector<double> deltas;
    for (uint32_t i = 1; i < n; i++)
      if (evs[i].qpc > evs[i - 1].qpc)
        deltas.push_back ((double) (evs[i].qpc - evs[i - 1].qpc) /
            (double) (qfreq.QuadPart ? qfreq.QuadPart : 1));
    if (!deltas.empty ()) {
      std::sort (deltas.begin (), deltas.end ());
      double median = deltas[deltas.size () / 2];
      double frame_s = media_fps > 0.0 ? 1.0 / media_fps : 0.0167;
      printf ("  pacing-median=%.2fms frame=%.2fms samples=%u\n",
          median * 1000.0, frame_s * 1000.0, n);
      const char* frame_hold = getenv ("TCS_TEST_HOLD_FRAME_LOCK_MS");
      bool frame_hold_on = frame_hold != nullptr && atoi (frame_hold) > 0;
      if (frame_hold_on)
        printf ("  pacing check skipped (TCS_TEST_HOLD_FRAME_LOCK_MS set)\n");
      else
        check (median > frame_s * 0.7 && median < frame_s * 1.3,
            "normal playback pacing matches media frame period");
    }
  }

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
  auto t_seek = std::chrono::steady_clock::now ();
  uint64_t gen2 = tcs_player_seek (p, target);
  check (gen2 == gen + 1, "seek bumps generation");
  TcsFrameInfo stale = {};
  check (tcs_player_acquire (p, gen, &stale) == 0, "old-gen acquire = none after seek");

  gen = gen2;
  got = 0;
  int acquire_misses = 0;
  for (int i = 0; i < 2000 && !got; i++) {
    got = tcs_player_acquire (p, gen, &info);
    if (!got) {
      acquire_misses++;
      std::this_thread::sleep_for (std::chrono::milliseconds (5));
    }
  }
  double seek_ms = std::chrono::duration<double, std::milli> (
      std::chrono::steady_clock::now () - t_seek).count ();
  printf ("  seek-to-lease=%.1fms acquire_misses=%d\n", seek_ms, acquire_misses);
  check (got == 1, "new-gen frame available after seek");
  check (seek_ms < 500.0, "seek-to-lease within 500ms");
  {
    /* first frame actually delivered after the seek (delivery trace), which
     * is independent of the acquire catch-up policy */
    std::vector<TcsDeliveryEvent> devs (512);
    uint32_t dn = 0;
    tcs_player_drain_delivery_events (p, devs.data (), (uint32_t) devs.size (), &dn);
    if (dn > 0) {
      double first_pts = devs[0].pts_ns / 1e9;
      printf ("  first-delivered pts=%.3f delta_ms=%.1f\n",
          first_pts, (first_pts - target) * 1000.0);
    }
  }
  if (got) {
    double pts_s = info.pts_ns / 1e9;
    double media_fps = 0.0;
    tcs_player_get_fps (p, &media_fps);
    double frame_s = media_fps > 0.0 ? 1.0 / media_fps : 0.040;
    printf ("  post-seek lease pts=%.3f target=%.3f delta_ms=%.1f frame_ms=%.1f seq=%llu\n",
        pts_s, target, (pts_s - target) * 1000.0, frame_s * 1000.0,
        (unsigned long long) info.seq);
    check (pts_s >= target - 0.001 && pts_s <= target + frame_s + 0.001,
        "lease pts within one frame of the seek target");
  }
  tcs_player_release (p);

  /* hold playback after the seek (compositor-like acquire/release) so the
   * A/V diagnostics window (TCS_SEEK_DIAG=1) can sample both streams */
  for (int h = 0; h < 200; h++) {
    TcsFrameInfo fi = {};
    if (tcs_player_acquire (p, gen, &fi) == 1)
      tcs_player_release (p);
    std::this_thread::sleep_for (std::chrono::milliseconds (10));
  }

  /* pause + step: generation bump per step */
  tcs_player_set_paused (p, 1);
  std::this_thread::sleep_for (std::chrono::milliseconds (100));
  auto t_step = std::chrono::steady_clock::now ();
  uint64_t gen3 = tcs_player_step_frame (p);
  double step_call_ms = std::chrono::duration<double, std::milli> (
      std::chrono::steady_clock::now () - t_step).count ();
  check (gen3 == gen2 + 1, "step bumps generation");
  got = 0;
  for (int i = 0; i < 200 && !got; i++) {
    got = tcs_player_acquire (p, gen3, &info);
    if (!got) std::this_thread::sleep_for (std::chrono::milliseconds (5));
  }
  double step_ms = std::chrono::duration<double, std::milli> (
      std::chrono::steady_clock::now () - t_step).count ();
  printf ("  step-call=%.1fms step-to-lease=%.1fms\n", step_call_ms, step_ms);
  check (got == 1, "stepped frame leased");
  check (step_ms < 500.0, "step-to-lease within 500ms");
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
