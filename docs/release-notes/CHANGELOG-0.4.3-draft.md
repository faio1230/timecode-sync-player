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
- Fixed Single mode seeking past the clip out-point when the LTC exceeded `MediaOut` (the seek used the media duration instead): the LTC-to-media mapping and the end clamp now use `MediaIn` / `MediaOut` (`MediaOut` unset falls back to the duration), and the end hold is `MediaOut`-based (D29).
- Fixed the freeze entered by a jump not showing the previous track's final frame: the frame is confirmed by position (PTS +/-2 frames) with up to 2 re-seeks (D21, D21-b).
- Fixed the black picture before the first track (leading offset) (D22).
- Fixed the one-frame black flash on jumps (seek / track switch) (D26, D26-b).
- Fixed held LTC (a repeated timecode) being ignored: Stop mode pauses at the held value, RunThrough keeps playing, and both resync on release. Recovery from a held loss needs one Jump frame, and the landing target on stop is the held value (D27, D27-b, D27-c, D27-d).
- Fixed paused seeks on long-GOP media timing out at the 500 ms pump deadline: the deadline is now "until the first frame, up to 4000 ms" (`TCS_PUMP_BUDGET_MS`) with a decode-progress warning, and frame step uses the same pump (D24).
- Fixed the first lease after a seek showing the pre-seek picture with the target PTS: pre-seek samples are excluded at the flush boundary, the shared fence value is monotonic for the player lifetime (D25), a lease whose fence is incomplete is retained and re-checked on the next tick (D25-b), and the paused position reports the newest frame PTS (D25-c).
- Fixed playback stopping as "unavailable" when the composition GPU wait exceeded 100 ms after a 4K VP9 paused seek on hybrid GPUs: the wait is sliced at 100 ms with a tick skip, resources are retained, and a permanent stop requires device loss or 3 continuous seconds (D28).
- Fixed a deferred (unconfirmed) Jump losing its confirmation when the audio capture stalled and the next frame arrived in a burst: the confirmation window is now judged by the frame-end timestamps (QPC derived from the audio sample position) and falls back to the wall clock only when they are unavailable (D31).
- Fixed Stop mode staying at the previous position when the held LTC value changed: a held value more than half a frame from the landed value now lands once, and RunThrough applies it once per change (D31-b).
- Fixed the C-1 scenario's landing check using a fixed target: the landing is now judged as the interval [target - 0.3, target + elapsed + 0.3 + 1 frame] (Stop mode +/-0.3), with 0.5 s of follow recorded (test-side).
- Fixed gap entries sometimes not updating the picture to the previous clip's final frame or the next clip's first frame: a freeze confirmation is re-confirmed when the target frame arrives after the 3-second timeout, and a changed entry target discards the previous frozen image and re-confirms (D32).
- Fixed Single mode continuing playback when the LTC went outside the clip's range (past `MediaOut` or before `MediaIn`): playback now holds at the edge position and resumes following when the LTC returns to range, and jump corrections are clamped to `[MediaIn, MediaOut]` (D33).
- Fixed fast (cached-profile) loads skipping the metadata fetch and leaving the on-screen metadata line showing the previous track: the fetch is now scheduled once when the load completes (D33-b).
- Fixed a rare case where the last successful decoder profile was reused for a different clip and the load was accepted with undetermined video caps (`loaded 0x0@0.000`) and no duration, leaving the picture and position stuck: the success gate now requires a frame of the attempt's own generation, non-zero width/height, no pad caps mismatch and no error; a failing attempt is reported as `caps-missing`, advances to the next profile and does not update the last-good profile. Variable-framerate media (framerate 0/1) is accepted with width/height only and logged (D34).
- Fixed the metadata line staying on the previous clip when the load returned without size: the metadata fetch is now retried for up to 5 seconds after a load (100 ms timer, with one warning when it times out) and the metadata line is cleared when a new track is loaded (D34).
- Fixed output trace saves failing when `manifest.json` already existed in the target directory (105 of 115 app starts on the verification machine): the trace is written to a `<startup-time>-<pid>` subfolder instead (D34).
- Fixed Stop mode stopping 0.2-0.5 seconds past the held LTC value (the advance until the loss timeout) with no landing seek: once the held value is known (regardless of the loss reason or whether it arrived before or after the pause), playback lands once on the held position without going through the sync tolerance, skipping only when already within one video frame. The Smooth rate is restored to 1.0 just before pausing (D35).
- Fixed the once-after-load "reapplying the last accepted timecode" firing 5-7 seconds after a load on an unrelated Reverse frame: the load release is forced 5 seconds after the load starts and a release older than 1.5 seconds is discarded instead of reapplied (D35).
- Fixed the S-3 scenario's end expectation clamping to the media duration instead of `MediaOut` like the product: it now clamps to `[MediaIn, MediaOut]`, which no longer overestimates the end on real media whose `MediaOut` is shorter than the duration (test-side).
- Fixed scenario E2E running with playback audio unmuted: the app's own audio (which may carry LTC) could loop back into the CABLE input and mix with the test signal; the scenarios now start muted (test-side).
- Fixed the track-switch completion check depending on the FetchMetadata line, which fast cached loads may not emit: completion now requires "Playlist track loaded index=N" together with the on-screen duration, because same-duration clips can match the duration on the pre-switch label (test-side).

### Known limitations

- 29.97 non-drop LTC requires the "Fixed 29.97" fps mode; Auto cannot distinguish it from 30.
- Recovering the shim from a real GPU device loss (TDR) is not supported in this release (D9).
- The product does not add a one-frame constant; adjust the sync offset (`syncOffsetMs`) for field delays.
- 120Hz output targets are not verified. A 4K main display may drop a few frames per 40 seconds (a dedicated display is recommended).
- Rare audio dropouts of up to 100 ms during track switches (4 of 12 in a 60-minute test).
- HAP is not supported.
- Codecs the integrated GPU cannot decode (AV1 and others) fall back to CPU decoding even when a dGPU is present (4K24 held up in the measurement; 4K60 not verified).
- On long-GOP media (for example 10-second keyframe intervals), a playing accurate seek must decode from the keyframe to the target, so jump landings can be 1-3 seconds late (measured with 60 fps H.264 at 10-second intervals). Recommended for production: encode with a 1-2 second keyframe interval (`-g 60..120` for 60 fps). Paused seeks wait up to 4 seconds (D24).
