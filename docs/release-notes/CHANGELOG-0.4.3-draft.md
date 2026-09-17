# CHANGELOG 0.4.3 draft

This file holds the draft for the `## 0.4.3 - 2026-09-18` section of `CHANGELOG.md`.
The parent integrates it into `CHANGELOG.md` (the file itself and the csproj `Version` are unchanged for now).

## 0.4.3 - 2026-09-18

### Added

- Added `TCS_PUMP_BUDGET_MS` (1..60000 ms, default 4000) to override the paused-seek pump deadline (D24).
- Added `compose.fencePending` trace events for a lease withheld until its ring fence completes (D25-b).
- Added E2E scenarios for held-LTC stop/run-through (R-1..R-4), black frames during jumps (C-1/C-2), freeze final frames (F-1..F-5), and gaps (G-1..G-6).

### Changed

- Held pictures are now compositor-owned copies of the last canvas instead of references to the ring; black is drawn only when the gap mode is Black (D26).
- While paused, `get_time_pos` reports the newest delivered video frame's stream-time PTS instead of the pipeline position (D25-c).
- GPU completion waits now skip the tick and retain resources; playback is marked unavailable only on device loss or 3 continuous seconds of incompletion (D28).

### Fixed

- Fixed Single mode with sync enabled advancing to the next track when the LTC passed the active track's end: playback stops at the end (D20).
- Fixed the freeze entered by a jump not showing the previous track's final frame: the frame is confirmed by position (PTS +/-2 frames) with up to 2 re-seeks (D21, D21-b).
- Fixed the black picture before the first track (leading offset) (D22).
- Fixed the one-frame black flash on jumps (seek / track switch) (D26, D26-b).
- Fixed held LTC (a repeated timecode) being ignored: Stop mode pauses at the held value, RunThrough keeps playing, and both resync on release. Recovery from a held loss needs one Jump frame, and the landing target on stop is the held value (D27, D27-b, D27-c, D27-d).
- Fixed paused seeks on long-GOP media timing out at the 500 ms pump deadline: the deadline is now "until the first frame, up to 4000 ms" (`TCS_PUMP_BUDGET_MS`) with a decode-progress warning, and frame step uses the same pump (D24).
- Fixed the first lease after a seek showing the pre-seek picture with the target PTS: pre-seek samples are excluded at the flush boundary, the shared fence value is monotonic for the player lifetime (D25), a lease whose fence is incomplete is retained and re-checked on the next tick (D25-b), and the paused position reports the newest frame PTS (D25-c).
- Fixed playback stopping as "unavailable" when the composition GPU wait exceeded 100 ms after a 4K VP9 paused seek on hybrid GPUs: the wait is sliced at 100 ms with a tick skip, resources are retained, and a permanent stop requires device loss or 3 continuous seconds (D28).

### Known limitations

- 29.97 non-drop LTC requires the "Fixed 29.97" fps mode; Auto cannot distinguish it from 30.
- Recovering the shim from a real GPU device loss (TDR) is not supported in this release (D9).
- The product does not add a one-frame constant; adjust the sync offset (`syncOffsetMs`) for field delays.
- 120Hz output targets are not verified. A 4K main display may drop a few frames per 40 seconds (a dedicated display is recommended).
- Rare audio dropouts of up to 100 ms during track switches (4 of 12 in a 60-minute test).
- HAP is not supported.
- Codecs the integrated GPU cannot decode (AV1 and others) fall back to CPU decoding even when a dGPU is present (4K24 held up in the measurement; 4K60 not verified).
