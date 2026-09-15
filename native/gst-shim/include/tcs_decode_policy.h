/* Decode profile order selection (V11 decode-mode switch, 2026-09-15).
 *
 * The shim builds the list of video profiles to try for one load. This helper
 * is pure so the native shim test can fix the order without media:
 *
 *   decodebin_only: the decodebin fallback alone (the container has no demuxer
 *     to pin profiles onto).
 *   hardware (default): last-good profile first, then every profile in table
 *     order, then the decodebin fallback (the behavior before V11, unchanged).
 *   software: CPU profiles first (last-good only when it is a CPU profile),
 *     then decodebin, then the GPU profiles as the last resort.
 *
 * `software_flags[i]` is nonzero for CPU decode profiles. The fallback index
 * is TCS_DECODE_PROFILE_FALLBACK. Returns the number of entries written to
 * `order`: 1 for decodebin_only, profile_count + 1 otherwise. The fallback
 * appears exactly once in every order. The caller passes an array of at least
 * profile_count + 1 entries and must not append to it afterwards.
 * License: MIT (same as the shim). */
#ifndef TCS_DECODE_POLICY_H
#define TCS_DECODE_POLICY_H

#define TCS_DECODE_PROFILE_FALLBACK (-1)

static inline int
tcs_decode_profile_order (int decodebin_only, int software_mode, int last_good,
                          const int* software_flags, int profile_count,
                          int* order)
{
  int n = 0;
  if (!decodebin_only) {
    if (software_mode) {
      if (last_good >= 0 && last_good < profile_count && software_flags[last_good])
        order[n++] = last_good;
      for (int i = 0; i < profile_count; i++)
        if (i != last_good && software_flags[i])
          order[n++] = i;
      order[n++] = TCS_DECODE_PROFILE_FALLBACK;
      for (int i = 0; i < profile_count; i++)
        if (!software_flags[i])
          order[n++] = i;
      return n;
    }
    if (last_good >= 0 && last_good < profile_count)
      order[n++] = last_good;
    for (int i = 0; i < profile_count; i++)
      if (i != last_good)
        order[n++] = i;
  }
  order[n++] = TCS_DECODE_PROFILE_FALLBACK;
  return n;
}

#endif /* TCS_DECODE_POLICY_H */
