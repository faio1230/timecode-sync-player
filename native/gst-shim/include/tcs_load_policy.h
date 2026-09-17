/* Load attempt success gate (D34, 2026-09-18).
 *
 * A load attempt may only count as successful when:
 *   - a frame of THIS attempt's generation arrived (a late frame of the
 *     previous pipeline, delivered while teardown set the old pipeline to
 *     NULL, carries the previous generation and must not satisfy the gate),
 *   - the video caps are determined (width/height both non-zero; a
 *     "loaded 0x0@0.000" stream is not a loaded stream),
 *   - there is no pad caps mismatch (a mismatched profile must not become
 *     last-good), no bus error and no policy rejection.
 *
 * fps is deliberately NOT part of the caps gate: variable-framerate
 * containers (webm/mkv and others) may report framerate=0/1, and such media
 * must still load. A missing framerate is logged once and the product keeps
 * its fps default.
 *
 * Pure so the native shim test can pin the truth table without media.
 * License: MIT (same as the shim). */
#ifndef TCS_LOAD_POLICY_H
#define TCS_LOAD_POLICY_H

static inline int
tcs_load_caps_ready (int width, int height)
{
  return width > 0 && height > 0;
}

static inline int
tcs_load_attempt_ok (int current_gen_frame, int caps_ready, int caps_mismatch,
                     int failed, int rejected)
{
  return current_gen_frame && caps_ready && !caps_mismatch && !failed && !rejected;
}

/* D34: an attempt that fails this gate is reported as caps-missing when the
 * caps never became usable (or the pad mismatched), otherwise as a preroll
 * timeout. */
static inline int
tcs_load_failure_is_caps_missing (int caps_ready, int caps_mismatch)
{
  return !caps_ready || caps_mismatch;
}

#endif /* TCS_LOAD_POLICY_H */
