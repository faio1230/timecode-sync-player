/* D10 (2026-09-17): map a buffer PTS to stream time for reported positions.
 *
 * qtdemux (MP4 with B-frames, first DTS < 0) sends a post-seek segment whose
 * start carries the first-DTS compensation ahead of the seek target while
 * pushing the correct IDR sample. Example from the real-media shim test:
 * seek target 15.000 -> segment start 15.033333, time 15.000, and the first
 * pushed buffer is the IDR with raw pts 15.033333. Reporting the raw PTS makes
 * the owner believe it is two frames ahead of where it actually is, so the
 * lease / frame log / delivery trace / position report use stream time
 * (`gst_segment_to_stream_time`), which maps 15.033333 back to 15.000.
 *
 * Running time (`gst_segment_to_running_time`) keeps using the raw PTS:
 * it is the display-scheduling clock and has different semantics.
 *
 * Fallback: no segment or an invalid mapping returns the raw PTS unchanged and
 * sets *used_fallback; the caller logs that once.
 * License: MIT (same as the shim). */
#ifndef TCS_TIME_MAPPING_H
#define TCS_TIME_MAPPING_H

#include <gst/gst.h>

static inline guint64
tcs_stream_time_or_pts (const GstSegment* seg, guint64 pts, int* used_fallback)
{
  if (used_fallback)
    *used_fallback = 0;
  if (seg != NULL && pts != GST_CLOCK_TIME_NONE) {
    guint64 stream = gst_segment_to_stream_time (seg, GST_FORMAT_TIME, pts);
    if (stream != GST_CLOCK_TIME_NONE)
      return stream;
  }
  if (used_fallback)
    *used_fallback = 1;
  return pts;
}

#endif /* TCS_TIME_MAPPING_H */
