/* Long-GOP warning tracker (0.4.5-C2, 2026-09-19).
 *
 * The owner warns when a video track's KEYFRAME INTERVAL is long enough to
 * make seek-based sync corrections unable to converge (see
 * docs/analysis/2026-09-19-V045C-design.md). This tracker is pure so the
 * native shim test can pin the rules without media or GStreamer:
 *
 *  - The clock is the buffer PTS (media time), never wall time, so decoder
 *    stalls and rate changes cannot produce a false warning.
 *  - The judgement is the MEDIAN of the measured keyframe-to-keyframe
 *    intervals, not the distance from the read position to the next keyframe.
 *    The old "no keyframe for N seconds" rule measured the latter and fired on
 *    variable-GOP material depending on where playback started (C2 fix).
 *  - At least TCS_GOP_MIN_INTERVALS intervals (3 keyframes) are required
 *    before a warning can latch; the warning then latches once and never
 *    clears before the next load. median > threshold (default 2.0 s).
 *  - The displayed measurement is the median (median_interval_ns).
 *  - reanchor() (issued by a seek) drops the anchor and waits for the next
 *    keyframe: the interval across a seek is not a property of the material,
 *    so it must not be recorded. Intervals already collected are kept.
 *  - Backward PTS (TS wrap, EOS restart) discards the interval and re-anchors.
 *
 * License: MIT (same as the shim). */
#ifndef TCS_GOP_POLICY_H
#define TCS_GOP_POLICY_H

#include <stdint.h>

#define TCS_GOP_STATE_MEASURING 0   /* median not above the threshold yet */
#define TCS_GOP_STATE_WARNING   1   /* median above the threshold (latched) */

/* Recent intervals used for the median. A ring keeps the struct POD and the
 * judgement stable against one-off short/long intervals. */
#define TCS_GOP_INTERVAL_CAPACITY 16
/* Minimum measured intervals before a warning can latch (3 keyframes). */
#define TCS_GOP_MIN_INTERVALS 2

typedef struct TcsGopTracker {
  int32_t  state;              /* TCS_GOP_STATE_* */
  int32_t  anchor_valid;
  int64_t  anchor_pts_ns;      /* last keyframe PTS; 0 while waiting after a re-anchor */
  int64_t  median_interval_ns; /* median keyframe-to-keyframe interval (0 = unconfirmed) */
  int64_t  pending_ns;         /* last observed buffer PTS - anchor PTS (diagnostics) */
  uint64_t keyframes;          /* keyframes observed since load */
  int64_t  threshold_ns;       /* default 2.0s, TCS_GOP_WARN_SECONDS override */
  uint64_t warning_qpc;        /* QPC when the warning latched (0 = none) */
  int64_t  intervals_ns[TCS_GOP_INTERVAL_CAPACITY]; /* ring of measured intervals */
  uint32_t interval_count;     /* intervals pushed since load (saturating) */
  uint32_t interval_next;      /* ring write index */
} TcsGopTracker;

static inline void
tcs_gop_tracker_init (TcsGopTracker* t, double threshold_sec)
{
  if (!t)
    return;
  t->state = TCS_GOP_STATE_MEASURING;
  t->anchor_valid = 0;
  t->anchor_pts_ns = 0;
  t->median_interval_ns = 0;
  t->pending_ns = 0;
  t->keyframes = 0;
  t->threshold_ns = (int64_t) (threshold_sec * 1e9);
  t->warning_qpc = 0;
  t->interval_count = 0;
  t->interval_next = 0;
  for (uint32_t i = 0; i < TCS_GOP_INTERVAL_CAPACITY; i++)
    t->intervals_ns[i] = 0;
}

/* Load: clear the measurement (threshold stays). */
static inline void
tcs_gop_tracker_reset (TcsGopTracker* t)
{
  if (!t)
    return;
  int64_t threshold = t->threshold_ns;
  tcs_gop_tracker_init (t, (double) threshold / 1e9);
}

/* Seek: wait for the next keyframe. The latched state and the measured
 * intervals are kept. */
static inline void
tcs_gop_tracker_reanchor (TcsGopTracker* t)
{
  if (!t)
    return;
  t->anchor_valid = 0;
  t->pending_ns = 0;
}

/* Median of the recent intervals (0 when none). Pure; no allocation. */
static inline int64_t
tcs_gop_tracker_median (const TcsGopTracker* t)
{
  int64_t tmp[TCS_GOP_INTERVAL_CAPACITY];
  uint32_t n = t->interval_count < TCS_GOP_INTERVAL_CAPACITY
      ? t->interval_count : TCS_GOP_INTERVAL_CAPACITY;
  if (n == 0)
    return 0;
  for (uint32_t i = 0; i < n; i++)
    tmp[i] = t->intervals_ns[i];
  for (uint32_t i = 1; i < n; i++)
    {
      int64_t value = tmp[i];
      uint32_t j = i;
      while (j > 0 && tmp[j - 1] > value)
        {
          tmp[j] = tmp[j - 1];
          j--;
        }
      tmp[j] = value;
    }
  return (n % 2) ? tmp[n / 2] : (tmp[n / 2 - 1] + tmp[n / 2]) / 2;
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
      t->intervals_ns[t->interval_next] = interval;
      t->interval_next = (t->interval_next + 1) % TCS_GOP_INTERVAL_CAPACITY;
      if (t->interval_count < UINT32_MAX)
        t->interval_count++;
      t->median_interval_ns = tcs_gop_tracker_median (t);
      {
        uint32_t n = t->interval_count < TCS_GOP_INTERVAL_CAPACITY
            ? t->interval_count : TCS_GOP_INTERVAL_CAPACITY;
        if (n >= TCS_GOP_MIN_INTERVALS &&
            t->median_interval_ns > t->threshold_ns &&
            t->state != TCS_GOP_STATE_WARNING)
          {
            t->state = TCS_GOP_STATE_WARNING;
            t->warning_qpc = now_qpc;
            return 1;
          }
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
  }
  return 0;   /* keyframe absence is not a warning any more (C2) */
}

#endif /* TCS_GOP_POLICY_H */
