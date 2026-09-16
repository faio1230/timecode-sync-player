# Changelog

All notable changes to TimecodeSyncPlayer are documented in this file.

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
