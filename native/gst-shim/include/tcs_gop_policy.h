/* Long-GOP warning tracker (0.4.5-C, 2026-09-19).
 *
 * The owner warns when a video track's keyframe interval is long enough to
 * make seek-based sync corrections unable to converge (see
 * docs/analysis/2026-09-19-V045C-design.md). This tracker is pure so the
 * native shim test can pin the rules without media or GStreamer:
 *
 *  - The clock is the buffer PTS (media time), never wall time, so decoder
 *    stalls and rate changes cannot produce a false warning.
 *  - The warning latches as soon as a gap >= threshold is observed, without
 *    waiting for the second keyframe (a 10 s GOP warns ~3 s after start).
 *  - A keyframe-to-keyframe interval >= threshold also latches, for streams
 *    whose non-key frames never reach the probe.
 *  - reanchor() (issued by a seek) drops the anchor and waits for the next
 *    keyframe, so a post-seek PTS jump cannot look like a long gap. The
 *    latched state is NOT cleared (a per-track, monotone warning).
 *  - Backward PTS (TS wrap, EOS restart) discards the interval and re-anchors.
 *
 * License: MIT (same as the shim). */
#ifndef TCS_GOP_POLICY_H
#define TCS_GOP_POLICY_H

#include <stdint.h>

#define TCS_GOP_STATE_MEASURING 0   /* no gap >= threshold observed yet */
#define TCS_GOP_STATE_WARNING   1   /* a gap >= threshold was observed (latched) */

typedef struct TcsGopTracker {
  int32_t  state;              /* TCS_GOP_STATE_* */
  int32_t  anchor_valid;
  int64_t  anchor_pts_ns;      /* last keyframe PTS; 0 while waiting after a re-anchor */
  int64_t  max_interval_ns;    /* max keyframe-to-keyframe interval (0 = unconfirmed) */
  int64_t  pending_ns;         /* last observed buffer PTS - anchor PTS */
  uint64_t keyframes;          /* keyframes observed since load */
  int64_t  threshold_ns;       /* default 3.0s, TCS_GOP_WARN_SECONDS override */
  uint64_t warning_qpc;        /* QPC when the warning latched (0 = none) */
} TcsGopTracker;

static inline void
tcs_gop_tracker_init (TcsGopTracker* t, double threshold_sec)
{
  if (!t)
    return;
  t->state = TCS_GOP_STATE_MEASURING;
  t->anchor_valid = 0;
  t->anchor_pts_ns = 0;
  t->max_interval_ns = 0;
  t->pending_ns = 0;
  t->keyframes = 0;
  t->threshold_ns = (int64_t) (threshold_sec * 1e9);
  t->warning_qpc = 0;
}

/* Load: clear the measurement (threshold stays). */
static inline void
tcs_gop_tracker_reset (TcsGopTracker* t)
{
  if (!t)
    return;
  t->state = TCS_GOP_STATE_MEASURING;
  t->anchor_valid = 0;
  t->anchor_pts_ns = 0;
  t->max_interval_ns = 0;
  t->pending_ns = 0;
  t->keyframes = 0;
  t->warning_qpc = 0;
}

/* Seek: wait for the next keyframe. The latched state is kept. */
static inline void
tcs_gop_tracker_reanchor (TcsGopTracker* t)
{
  if (!t)
    return;
  t->anchor_valid = 0;
  t->pending_ns = 0;
}

/* One probe observation. Returns 1 when the warning latched on THIS call. */
static inline int
tcs_gop_tracker_observe (TcsGopTracker* t, int is_keyframe, int64_t pts_ns,
                         uint64_t now_qpc)
{
  if (!t || pts_ns < 0)
    return 0;

  if (!t->anchor_valid)
    {
      /* Wait for a keyframe: a GOP entered mid-way after a seek must not be
       * mistaken for a long interval. */
      if (is_keyframe)
        {
          t->anchor_pts_ns = pts_ns;
          t->anchor_valid = 1;
          t->pending_ns = 0;
          t->keyframes++;
        }
      return 0;
    }

  if (is_keyframe)
    {
      int64_t interval = pts_ns - t->anchor_pts_ns;
      t->keyframes++;
      t->anchor_pts_ns = pts_ns;
      t->pending_ns = 0;
      if (interval < 0)
        return 0;   /* backward PTS: discard the interval, anchor here */
      if (interval > t->max_interval_ns)
        t->max_interval_ns = interval;
      if (interval >= t->threshold_ns && t->state != TCS_GOP_STATE_WARNING)
        {
          t->state = TCS_GOP_STATE_WARNING;
          t->warning_qpc = now_qpc;
          return 1;
        }
      return 0;
    }

  {
    int64_t pending = pts_ns - t->anchor_pts_ns;
    if (pending < 0)
      {
        /* backward PTS: discard the interval and wait for a keyframe */
        t->anchor_valid = 0;
        t->pending_ns = 0;
        return 0;
      }
    t->pending_ns = pending;
    if (pending >= t->threshold_ns && t->state != TCS_GOP_STATE_WARNING)
      {
        t->state = TCS_GOP_STATE_WARNING;
        t->warning_qpc = now_qpc;
        return 1;
      }
  }
  return 0;
}

#endif /* TCS_GOP_POLICY_H */
