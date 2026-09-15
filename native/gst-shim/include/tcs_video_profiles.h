/* Video profile table (V11). Pure data plus the CPU/GPU classifier, kept in a
 * header so the native test can pin the classification of the real table.
 *
 * The chain builder derives CPU vs GPU from `conv` (a "d3d11" converter means a
 * GPU profile). A CPU profile whose conv were changed to d3d11colorconvert
 * would be treated as GPU and lose d3d11upload, so CPU conversion stays in
 * `conv` as videoconvert; V11-h performs the GPU conversion with a separate
 * post-upload element (see build_video_chain_static). */
#ifndef TCS_VIDEO_PROFILES_H
#define TCS_VIDEO_PROFILES_H

#include <string.h>

typedef struct TcsVideoProfile {
  const char* name;
  const char* media;      /* stream media-type prefix, e.g. "video/x-h264" */
  const char* media2;     /* optional alias, e.g. "video/x-hevc" */
  const char* parse;      /* may be NULL (raw pad / self-parsing decoder) */
  const char* dec;        /* may be NULL (already-raw pad) */
  const char* conv;       /* color converter element name */
} TcsVideoProfile;

/* GPU profiles first, then explicit CPU decoders (uploaded to the ring). An
 * unmatched video pad retries with the decodebin fallback. */
static const TcsVideoProfile kTcsVideoProfiles[] = {
  { "h264-gpu", "video/x-h264", nullptr, "h264parse", "d3d11h264dec", "d3d11colorconvert" },
  { "h265-gpu", "video/x-h265", "video/x-hevc", "h265parse", "d3d11h265dec", "d3d11colorconvert" },
  { "vp9-gpu",  "video/x-vp9",  nullptr, "vp9parse", "d3d11vp9dec", "d3d11colorconvert" },
  { "av1-gpu",  "video/x-av1",  nullptr, "av1parse", "d3d11av1dec", "d3d11colorconvert" },
  { "h264-cpu", "video/x-h264", nullptr, "h264parse", "avdec_h264", "videoconvert" },
  { "h265-cpu", "video/x-h265", "video/x-hevc", "h265parse", "avdec_h265", "videoconvert" },
  { "vp9-cpu",  "video/x-vp9",  nullptr, "vp9parse", "avdec_vp9", "videoconvert" },
  { "av1-cpu",  "video/x-av1",  nullptr, "av1parse", "dav1ddec", "videoconvert" },
  { "prores-cpu", "video/x-prores", nullptr, nullptr, "avdec_prores", "videoconvert" },
};

#define TCS_VIDEO_PROFILE_COUNT \
  ((int) (sizeof (kTcsVideoProfiles) / sizeof (kTcsVideoProfiles[0])))

/* CPU decode profiles (decode to sysmem, uploaded with d3d11upload) versus
 * GPU profiles (d3d11*dec -> d3d11colorconvert). Derived from the converter
 * so reordering the table stays safe. */
static inline int
tcs_video_profile_is_software (int idx)
{
  return idx >= 0 && idx < TCS_VIDEO_PROFILE_COUNT &&
      strstr (kTcsVideoProfiles[idx].conv, "d3d11") == nullptr;
}

#endif /* TCS_VIDEO_PROFILES_H */
