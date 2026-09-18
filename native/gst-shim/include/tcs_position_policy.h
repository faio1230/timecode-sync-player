/* Delivered-PTS fallback gate (0.4.5-A phase 2, 2026-09-19).
 *
 * tcs_player_get_time_pos falls back to the newest delivered video frame's
 * stream-mapped PTS when gst_element_query_position fails. That query fails
 * 0.3-0.6 ms after a seek is issued (measured in phase 1), while latest_gen
 * still points at the pre-seek pipeline; the fallback would then hand the
 * owner a position from before the seek. Phase 1 tolerated it because
 * PlaybackPositionTrust suppressed decisions while a seek was pending, but
 * phase 2 evaluates position feedback directly, so the fallback must only use
 * a frame of the CURRENT generation.
 *
 * The rule is pure so the native shim test can pin it without media:
 * allowed when a delivered frame exists and its generation equals the current
 * one. When not allowed the caller reports TCS_ERR_NOT_LOADED (no position),
 * NOT a value with basis NONE: the legacy getter has no basis field, and 0.0
 * is a valid media position, so a zero-valued "unknown" would be read as a
 * real position by callers that only look at the seconds.
 *
 * License: MIT (same as the shim). */
#ifndef TCS_POSITION_POLICY_H
#define TCS_POSITION_POLICY_H

#include <stdint.h>

/* 1 = the delivered PTS may be returned as the current position.
 * latest_generation is the generation of the newest delivered frame (0 = no
 * frame); current_generation is the player generation at the same instant. */
static inline int
tcs_position_fallback_allowed (uint64_t latest_generation, uint64_t current_generation)
{
  return latest_generation != 0 && latest_generation == current_generation;
}

#endif /* TCS_POSITION_POLICY_H */
