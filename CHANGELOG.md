# Changelog

All notable changes to TimecodeSyncPlayer are documented in this file.

## 0.4.5 - 未定

0.4.4 was never published; its entries are folded in here because the two are a single
dependency chain (the long-GOP warning explains the condition the seek fixes work around).

### Added

- A material whose keyframe interval is long is flagged when it is loaded, in the status line
  and in the playlist. The judgement reads the container (no decoding: 0.3s for a 928MB 4K60 file,
  22.8s on the first read of a 13.7GB one and 5.8 to 6.3s after that) and uses the **longest** gap, including the head gap (0 to the first keyframe) and the
  tail gap (last keyframe to the end). Material whose median sits inside the recommendation can
  still hold a 7s gap, and seeking into it is slow: measured 244ms right after a keyframe
  against 2164ms just before the next one. Playback is not stopped and this is not an error.
- `scripts/inspect-gop.ps1` reports the same distribution for files or folders without running
  the app. Requires ffprobe.
- `docs/USER-MANUAL.md`: a field preparation guide covering the recommended material format
  (keyframe interval, codec, fps), the timecode signal conditions, how to read the warning, and
  what to check when sync misbehaves. Every figure in it is measured; items that were not
  measured (bitrate) say so.
- Output traces now record the age breakdown from the LTC sample end to the sync evaluation
  (Dispatcher enqueue, queue wait, UI processing) when the accuracy trace is enabled (M1).
  Behaviour is unchanged.

### Changed

- While a seek has not been confirmed to have landed, the position used for sync decisions is
  derived from the frame actually on screen. The position query returns the seek target during
  that window, so a growing error was invisible (measured: 0.39s growing to 0.75s before the
  landing). Off by default; `TCS_SYNC_POSITION_FEEDBACK=on` enables it. Steady playback is
  unchanged (the two differ by less than a sixth of a frame there).

### Fixed

- Fixed correction seeks firing on momentary measurement jitter: the coarse decision now uses a recent median with a consecutive-exceedance gate and rejects physically impossible spikes (D37-a). On material with a long keyframe interval (4K60, 6.6s between keyframes) this removes the seek chain and the associated freezes of up to 1.8 seconds. **The cause is the keyframe interval, not the resolution or the bitrate**: at the same 4K60, material with a 0.5s interval catches up in 1.4s from a 5.8s offset and issues no seek at all, while 6.6s material takes 16s.
- Fixed the UI update scheduler rarely losing one reschedule request: the request handoff is now a single atomic operation (D36). The picture never stopped and the worst remaining delay was about 100 ms, but the loss itself is gone.
- A real shortfall during follow is now closed by nudging the playback rate instead of seeking, so a lag of around half a second clears within a few seconds without freezing the picture. Gap exits and track-switch landings still seek, because the picture is black at that moment and a seek lands faster than a rate ramp (D37-b).
- The reported playback position is no longer used for decisions until a seek is confirmed to have landed. During a seek the position query can be off by 0.5 to 0.8 seconds, and that value was triggering the next seek (D37-b).
- Fixed the offset present when sync starts following being closed by playback rate instead of by seeking. On long-GOP media the initial offset can exceed a second, and closing it by rate took close to 20 seconds, during which the picture ran about 10% fast. Like a gap exit or a track switch, nothing meaningful is on screen at that moment, so a seek lands faster (D37-c).
- Rate correction now rejects momentary measurement spikes as well. D37-a shielded only the seek decision; the rate path was still fed the raw values (D37-c).
- The follow-start landing no longer ends after a single seek: it continues until the error is inside tolerance. On long-GOP media the first seek takes 1.8 to 2 seconds and leaves the same error behind, so returning to the normal decision there could take 19 seconds (D37-d).
- The follow-start seek now aims ahead by how far the timecode will advance before the landing. On media where a seek takes 2 seconds the landing was already 2 seconds behind, so no number of seeks converged (D37-e).
- Deriving that estimate from the keyframe interval read at load time is implemented but **off by default in this release** (D37-f / D37-h; see Not shipped below).
- The follow-start lookahead no longer leaks into unrelated seeks. While Single mode holds an out-of-range timecode the position is pinned to the clip boundary, so the error can never fall inside tolerance and the follow-start landing never closed; the recovery seek taken when the timecode came back into range then overshot by the lookahead (measured 0.3s). That scenario also has a stopped timecode, so no later correction ran and the position kept drifting (D37-g).

### Not shipped

- **Estimating the seek duration from the keyframe interval and aiming that far ahead is off by default** (D37-h). It was meant to speed up the start of following on material with a long keyframe interval, but it broke more than it fixed.
  - The estimate is `longest keyframe interval x coefficient`, which assumes **the landing falls at the worst place in the file**. In practice it lands near a keyframe, and then it **overshoots badly**: measured on the test machine, the first seek of a follow-start overshot on **12 runs out of 12, by +2.53 to +2.69 seconds** (estimate 2.79s against an actual cost of 0.19 to 0.31s).
  - Every run then needed one backward seek to undo the overshoot, and that seek landed in a bad region and took over 2 seconds.
  - **Five scenario tests that had been passing started failing** (the landing position fell outside the test tolerance), and the material it was meant to help still exceeded the criterion on 1 run in 13.
  - **Tuning the coefficient does not fix this.** Any single-point estimate built on "the worst place in the file" keeps the same spread. The next release replaces it with a target chosen from the keyframe positions themselves.
  - `TCS_SEEK_COST_HINT=on` enables it for measurement. **Do not use it in the field.**

### Known limitations

- On material with a long keyframe interval (10s between keyframes, say), an accurate seek during playback decodes from the keyframe to the target, so a jump can land 1 to 3 seconds late (measured on 60fps H.264 with a 10s interval). **Recommended for field use: export with a keyframe interval of 1 to 2 seconds (`-g 60` to `-g 120` at 60fps).** A seek while paused waits up to 4 seconds on the D24 pump budget (`TCS_PUMP_BUDGET_MS`).
- On material with a long keyframe interval, a lag that appears while following is closed by playback rate rather than by seeking, so it can take several to a dozen seconds to clear (roughly 10 seconds per second of lag). Seeking there does not converge, because the landing carries a new error equal to the seek duration. Shortening the keyframe interval makes seeks fast and keeps this state from arising.
- **When the timecode fps and the material fps do not match, a constant spread remains.** When they match (25fps material with 25fps timecode, say) the spread disappears and the error becomes a fixed one-frame offset. The further the ratio is from 1, the larger it gets (about 17ms for 60fps material with a 30fps timecode, about 27ms with a 25fps timecode). **Recommended for field use: match the material fps to the timecode fps where possible.**
- Switching tracks takes 0.7 to 0.8 seconds before the first picture appears (measured on 4K60 field material: 0.42s to load, 0.3 to 0.4s to the first picture). A switch across a gap hides this because the screen is black, but **a switch with no gap leaves the previous picture on screen for 0.7 to 0.8 seconds**.
- AV1 material gets no in-app keyframe-interval warning: the parser used for the scan does not mark keyframes, so the interval cannot be read (playback itself works). Long-GOP AV1 is therefore undetectable in the app, and H.264 is recommended for field use. `scripts/inspect-gop.ps1` does read AV1 correctly (it uses ffprobe; verified on a 3840x2160 AV1 file, 12 keyframes, 1.000s maximum gap), so AV1 material can be checked before a show.
- **On material with a long keyframe interval, sync takes around 7 seconds to lock on after following starts** (measured on field material with a 6.6s interval, 16 runs under the same conditions: **6.7 to 8.2s, median 7.3s**). A single seek leaves an error equal to the seek duration, so the landing is repeated until it converges. **A keyframe interval of 1 to 2 seconds brings this down to about 1.4 seconds.**
- **Occasionally the playback rate swings up and down and the picture actually runs fast.** The playback position used to measure the sync error returns two values about 0.4s apart within a few milliseconds of each other (measured: 140 backward steps of 50ms or more in 8.4 seconds, median 0.43s). While it lasts the rate spends **46% of the time at +9% or more and 30% at -9% or less**, and one 8-second stretch ran **10.2% fast throughout** (the average over the whole follow is 0.998, so the average hides it). The picture itself stays smooth (16.7ms between updates, no gaps).
  - Seen on 2 of 8 runs under the same conditions, on material with a long keyframe interval.
  - **The cause is in how the playback position is obtained and predates this release.** It is not fixed here: fixing it means changing where the position comes from, which needs a full re-verification.
  - **It is believed to predate 0.4.5** (the position is obtained the same way as before, and closing the error by playback rate has been the default since v0.4.0). **That is a judgement from the code history, not a measurement on an earlier release.** Closing more of the error by rate in this release may make it surface more often.
- Over a 10-minute continuous follow the error widens temporarily a few times (measured: 4 times in 10 minutes, 0.61s at worst, 1.3s of stopped picture in total). Each one is recovered by a single seek and does not chain. **A 60-second test never shows this**, which is why it had not been observed before. Longer continuous runs remain to be checked.

## 0.4.3 - 2026-09-18

### Added

- Added `TCS_PUMP_BUDGET_MS` (1..60000 ms, default 4000) to override the paused-seek pump deadline (D24).
- Added `compose.fencePending` trace events for a lease withheld until its ring fence completes (D25-b).

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
- Fixed gap entries sometimes not updating the picture to the previous clip's final frame or the next clip's first frame: a freeze confirmation is re-confirmed when the target frame arrives after the 3-second timeout, and a changed entry target discards the previous frozen image and re-confirms (D32).
- Fixed Single mode continuing playback when the LTC went outside the clip's range (past `MediaOut` or before `MediaIn`): playback now holds at the edge position and resumes following when the LTC returns to range, and jump corrections are clamped to `[MediaIn, MediaOut]` (D33).
- Fixed fast (cached-profile) loads skipping the metadata fetch and leaving the on-screen metadata line showing the previous track: the fetch is now scheduled once when the load completes (D33-b).
- Fixed a rare case where the last successful decoder profile was reused for a different clip and the load was accepted with undetermined video caps (`loaded 0x0@0.000`) and no duration, leaving the picture and position stuck: the success gate now requires a frame of the attempt's own generation, non-zero width/height, no pad caps mismatch and no error; a failing attempt is reported as `caps-missing`, advances to the next profile and does not update the last-good profile. Variable-framerate media (framerate 0/1) is accepted with width/height only and logged (D34).
- Fixed the metadata line staying on the previous clip when the load returned without size: the metadata fetch is now retried for up to 5 seconds after a load (100 ms timer, with one warning when it times out) and the metadata line is cleared when a new track is loaded (D34).
- Fixed output trace saves failing when `manifest.json` already existed in the target directory (105 of 115 app starts on the verification machine): the trace is written to a `<startup-time>-<pid>` subfolder instead (D34).
- Fixed Stop mode stopping 0.2-0.5 seconds past the held LTC value (the advance until the loss timeout) with no landing seek: once the held value is known (regardless of the loss reason or whether it arrived before or after the pause), playback lands once on the held position without going through the sync tolerance, skipping only when already within one video frame. The Smooth rate is restored to 1.0 just before pausing (D35).
- Fixed the once-after-load "reapplying the last accepted timecode" firing 5-7 seconds after a load on an unrelated Reverse frame: the load release is forced 5 seconds after the load starts and a release older than 1.5 seconds is discarded instead of reapplied (D35).
- Fixed the position remaining at the clip edge after a clip-boundary hold: the D35 explicit landing to the held value created a pending seek to the edge during the hold, and the landing for an LTC that returned into range after the hold was released was suppressed. The explicit landing is no longer issued during a boundary hold, and the pending seek and the hold-landing latch are cleared when the hold is released (D35-b).

### Known limitations

- 29.97 non-drop LTC requires the "Fixed 29.97" fps mode; Auto cannot distinguish it from 30.
- Recovering the shim from a real GPU device loss (TDR) is not supported in this release (D9).
- The product does not add a one-frame constant; adjust the sync offset (`syncOffsetMs`) for field delays.
- 120Hz output targets are not verified. A 4K main display may drop a few frames per 40 seconds (a dedicated display is recommended).
- Rare audio dropouts of up to 100 ms during track switches (4 of 12 in a 60-minute test).
- HAP is not supported.
- Codecs the integrated GPU cannot decode (AV1 and others) fall back to CPU decoding even when a dGPU is present (4K24 held up in the measurement; 4K60 not verified).
- On long-GOP media (for example 10-second keyframe intervals), a playing accurate seek must decode from the keyframe to the target, so jump landings can be 1-3 seconds late (measured with 60 fps H.264 at 10-second intervals). Recommended for production: encode with a 1-2 second keyframe interval (`-g 60..120` for 60 fps). Paused seeks wait up to 4 seconds (D24).

## 0.4.2 - 2026-09-17

### Changed

- Decoder memory from another adapter is no longer passed to the shim context: the load fails instead of crashing in the driver (`on_new_sample` guard).

### Fixed

- Fixed the crash inside the AMD driver on hybrid GPUs when the ring and a d3d11 decoder ran on different adapters: GPU profiles whose decoder GUID is missing on the shim adapter are skipped in favour of the CPU profile (D16, present since v0.4.0).
- Fixed the decoder element being taken from the wrong adapter class: the class matching the ring adapter (`d3d11…device{N}dec`) is selected (D16-b).
- Fixed a 3-second wait per mismatched profile: a pad caps mismatch now ends the attempt at once. AV1 4K load dropped from 18.8 seconds to about 1 second (D17).

### Known limitations

- 29.97 non-drop LTC requires the "Fixed 29.97" fps mode; Auto cannot distinguish it from 30.
- Recovering the shim from a real GPU device loss (TDR) is not supported in this release (D9).
- The product does not add a one-frame constant; adjust the sync offset (`syncOffsetMs`) for field delays.
- 120Hz output targets are not verified. A 4K main display may drop a few frames per 40 seconds (a dedicated display is recommended).
- Rare audio dropouts of up to 100 ms during track switches (4 of 12 in a 60-minute test).
- HAP is not supported.
- Codecs the integrated GPU cannot decode (AV1 and others) fall back to CPU decoding even when a dGPU is present (4K24 held up in the measurement; 4K60 not verified).

## 0.4.1 - 2026-09-17

### Added

- Added `TCS_LOG_FILE`: the application points the shim log at `logs\tcs-gst-YYYYMMDD.log` and removes files older than 7 days.
- Added E2E media and tests for 44.1kHz audio and for MP4 files whose video track comes first.

### Fixed

- Fixed 44.1kHz audio media failing to load: the audio branch now resamples to the device rate with `audioresample` (D12).
- Fixed preroll stalling for MP4 files with the video track first: the video branch now has a queue directly after the demux (D13).
- Fixed mismatched decode profiles waiting for the full timeout: the attempt now aborts as soon as the bus reports an error (D14).
- Fixed the packaged GStreamer runtime missing `gstaudioresample.dll`, `gsttypefindfunctions.dll`, and `gio-2.0-0.dll` (D15).

### Known limitations

- 29.97 non-drop LTC requires the "Fixed 29.97" fps mode; Auto cannot distinguish it from 30.
- Recovering the shim from a real GPU device loss (TDR) is not supported in this release (D9).
- The product does not add a one-frame constant; adjust the sync offset (`syncOffsetMs`) for field delays.
- 120Hz output targets are not verified. A 4K main display may drop a few frames per 40 seconds (a dedicated display is recommended).
- Rare audio dropouts of up to 100 ms during track switches (4 of 12 in a 60-minute test).
- HAP is not supported.

## 0.4.0 - 2026-09-17

### Added

- Added a typed playback API (`IPlaybackApi`, `PlaybackResult`, `IRenderUpdateSource`) and removed string commands and property names from the application boundary.
- Made GStreamer-based playback and GPU composition the default. Decoded textures arrive through a D3D11 shared ring and stay on the GPU through composition, fullscreen presentation, and Spout output.
- Added support for CPU-decoded codecs (ProRes and others) on the same lease path through `d3d11upload`.
- Added playback of media with audio tracks (S1).
- Made MPEG-TS seeking land accurately with keyframe snapping and segment rebasing (S3).
- Added LTC fps modes (Auto and Fixed 24 / 25 / 29.97 / 30) and a sync offset (`syncOffsetMs`, -1000 to +1000 ms).
- Added sync correction modes (Smooth / Jump). The Smooth limit is widened to +/-20% for one second after landing to shorten convergence (T9).
- Added the sample clock (`TCS_LTC_SAMPLE_CLOCK`, on by default) that evaluates LTC time from the audio sample position (T2).
- Added ring epoch tracking so the shared ring follows resolution changes and is reopened when the epoch changes (D8).

### Changed

- Removed mpv. Playback runs on GStreamer only and the `backend` setting is gone. Use `decodeMode` (`hardware` / `software`) as the fallback.
- Removed CPU composition. Output is GPU composition only and the `outputBackend` default is `1` (Gpu). `0` (Cpu) is ignored with a warning and the settings file is not rewritten.
- Renamed runtime-specific type names to role-based names (previously `IMpvApi` and others).
- Made sample-based analysis (frame-end reference) the official analysis for V3 (LTC sync accuracy).
- Pinned the pipeline clock to the system clock and slaved audio sinks to it (S3 side effect).
- Removed the runtime name from the shutdown step name ("GStreamer 停止").
- Single mode now treats the clip `MediaOut` as the end of playback, consistent with Continue mode.

### Removed

- Removed the mpv backend and the bundled `libmpv-2.dll` / `mpv-2.dll`.
- Removed the `backend` key and `outputBackend=Cpu` (v0.3 values are ignored at startup with a warning).
- Removed the debug OSD (`showDebugOsd` overlay; the setting key remains for compatibility).
- Removed the old mpv compatibility adapters and the string command path (`GstMpvApiAdapter`, `GstCommandTranslator`, `MpvPlaybackCommandBuilder`, and others).

### Fixed

- Fixed loading of MP4 files with audio tracks (S1).
- Fixed loading of CPU-decoded codecs such as ProRes (S2).
- Fixed MPEG-TS seeks that took several seconds before frames arrived (S3).
- Fixed playback not progressing after GPU recovery when a track switch changed the resolution (D8).
- Fixed MP4 files with B frames reporting a position ahead of the real one after an accurate seek: the shim now uses stream time mapped through the segment instead of the raw buffer PTS (D10).
- Fixed stale re-application of LTC frames issuing sync requests when the gap behavior changed (U1).
- Fixed the `ProjectRoundTrip` E2E failure (test-side playlist selection wait; Q1).
- Fixed Single mode staying in the "seeking" state after a seek to EOF, which blocked later sync seeks when the LTC was rewound (D11).
- Fixed the position label and seek bar not refreshing right after a positioned load while paused (F1).

### Known limitations

- 29.97 non-drop LTC requires the "Fixed 29.97" fps mode; Auto cannot distinguish it from 30.
- Recovering the shim from a real GPU device loss (TDR) is not supported in this release (D9).
- The product does not add a one-frame constant; adjust the sync offset (`syncOffsetMs`) for field delays.
- 120Hz output targets are not verified. A 4K main display may drop a few frames per 40 seconds (a dedicated display is recommended).
- Rare audio dropouts of up to 100 ms during track switches (4 of 12 in a 60-minute test).
- HAP is not supported.

## 0.3.0 - 2026-07-18

### Added

- Added an explicit `NO SIGNAL` state with a muted timecode color while retaining the last received LTC value.
- Added the next enabled track name and timeline start to Continue-mode gap labels, with a clear no-following-track state.
- Added a policy-owned pause reason near the playback controls when Stop mode pauses playback after LTC signal loss.

### Changed

- Moved mpv software rendering to a dedicated render thread, reducing QA-002 UI Automation response time from approximately 10.5 seconds to 24 milliseconds while playback is active.

### Known limitations

- After LTC monitoring stops, the LTC display intentionally retains the final received value.

## 0.2.0 - 2026-07-16

### Added

- Added the `showDebugOsd` setting for opt-in playback time and media metadata overlays.
- Added selectable full-screen video output for connected external displays.
- Added the `fullscreenDisplayDeviceName` setting to restore the selected output display.
- Added persistent mute and volume controls that remain unchanged across playback, track, and gap transitions.

### Fixed

- Double-clicking the playlist scrollbar or empty area no longer starts playback of the selected track.
- Switching to Single mode or turning Sync off now clears an active gap Freeze/Black state, so manual playback and seeking work instead of staying on the gap frame.

### Changed

- Debug OSD is hidden by default while seek feedback via the mpv OSD bar remains available.
- Split the README into dedicated Japanese and English editions with streamlined release and setup guidance.
- Enabled PerMonitorV2 DPI awareness for sharp, correctly positioned output across displays with different scaling.

### Known limitations

- After LTC monitoring stops, the LTC display intentionally retains the final received value.

## 0.1.0 - 2026-07-15

Initial beta release for validation in real show environments.

### Features

- Frame-based video playback synchronized to LTC audio input.
- Single and Continue synchronization modes with clip timeline offsets.
- Freeze or Black display behavior across timecode gaps.
- Run-through or Stop behavior when valid LTC frames are no longer received.
- Configurable LTC frame-rate interpretation and automatic frame-rate detection.
- Playlist and project save/load workflows.
- Spout2 video output for VJ and compositing tools.
- Per-user persistence of window, timeline, offset, LTC device, and synchronization settings.
- Pure C# LTC decoder with degraded-signal hardware-loop coverage.

### Known limitations

- Windows 10/11 x64 only; other platforms are not supported.
- libmpv is required but is not included in the release zip. Run the bundled
  `scripts/get-mpv.ps1` after extracting the package, or install a compatible x64 DLL manually.
- LTC reliability depends on the audio interface, operating-system audio routing, level, and noise.
  Validate the complete hardware and show files before production use.
- Changes made directly to the LTC signal-loss timeout or resume-frame settings require an
  application restart.
- This is a beta release. Tagging, release publication, and the full manual venue checklist remain
  release-operator steps.
