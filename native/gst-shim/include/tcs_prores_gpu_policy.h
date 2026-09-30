/* v0.6.0: ProRes GPU decode gate (design 3-3) and the decoder adapter
 * mismatch rule (design 3-2, stage 2).
 *
 * The gate decides whether the prores-gpu profile may be tried, from the
 * mode (tcs_player_set_prores_gpu / TCS_PRORES_GPU) and the PCI vendor id of
 * the adapter the shim device runs on (the adapter that actually decodes):
 *
 *   mode \ vendor | NVIDIA (0x10DE) | other | unknown (0)
 *   auto          | yes             | no    | no
 *   on            | yes             | yes   | yes (logged as unverified)
 *   off           | no              | no    | no
 *
 * Pure so the native shim test can pin the truth table without a GPU.
 * License: MIT (same as the shim). */
#ifndef TCS_PRORES_GPU_POLICY_H
#define TCS_PRORES_GPU_POLICY_H

#include "tcs_gstreamer.h"

#define TCS_PCI_VENDOR_NVIDIA 0x10DEu

static inline int
tcs_prores_gpu_mode_valid (int mode)
{
  return mode == TCS_PRORES_GPU_AUTO || mode == TCS_PRORES_GPU_ON ||
      mode == TCS_PRORES_GPU_OFF;
}

static inline int
tcs_prores_gpu_allowed (int mode, unsigned vendor_id)
{
  if (mode == TCS_PRORES_GPU_ON)
    return 1;
  if (mode == TCS_PRORES_GPU_AUTO)
    return vendor_id == TCS_PCI_VENDOR_NVIDIA;
  return 0;
}

/* The load.skip reason when the gate is closed, NULL when it is open. */
static inline const char*
tcs_prores_gpu_skip_reason (int mode, unsigned vendor_id)
{
  if (tcs_prores_gpu_allowed (mode, vendor_id))
    return nullptr;
  return mode == TCS_PRORES_GPU_AUTO ? "prores-gpu-vendor" : "prores-gpu-off";
}

/* mode=on forces the GPU path on an adapter it was not verified on. */
static inline int
tcs_prores_gpu_unverified (int mode, unsigned vendor_id)
{
  return mode == TCS_PRORES_GPU_ON && vendor_id != TCS_PCI_VENDOR_NVIDIA;
}

static inline const char*
tcs_prores_gpu_mode_name (int mode)
{
  switch (mode) {
    case TCS_PRORES_GPU_AUTO: return "auto";
    case TCS_PRORES_GPU_ON: return "on";
    case TCS_PRORES_GPU_OFF: return "off";
    default: return "?";
  }
}

/* "auto" / "on" / "off", ASCII case-insensitive. -1 for anything else
 * (NULL and the empty string included). */
static inline int
tcs_prores_gpu_mode_parse (const char* text)
{
  static const char* const names[] = { "auto", "on", "off" };
  static const int modes[] = { TCS_PRORES_GPU_AUTO, TCS_PRORES_GPU_ON, TCS_PRORES_GPU_OFF };
  if (!text)
    return -1;
  for (int n = 0; n < 3; n++) {
    const char* a = text;
    const char* b = names[n];
    while (*a && *b) {
      char c = *a;
      if (c >= 'A' && c <= 'Z')
        c = (char) (c - 'A' + 'a');
      if (c != *b)
        break;
      a++;
      b++;
    }
    if (*a == '\0' && *b == '\0')
      return modes[n];
  }
  return -1;
}

/* Stage 2: a prores-gpu attempt fails as decoder-adapter-mismatch when the
 * first decoder output was not on the shim device (same == 0; -1 = the probe
 * has not reported), or when, after a frame of this attempt arrived, the
 * adapter-luid read back from the decoder differs from the shim LUID. */
static inline int
tcs_prores_gpu_adapter_mismatch (int same, int frame, long long read_luid,
                                 long long want_luid)
{
  if (same == 0)
    return 1;
  return frame && read_luid != want_luid;
}

#endif /* TCS_PRORES_GPU_POLICY_H */
