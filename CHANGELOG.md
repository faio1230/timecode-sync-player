# Changelog

All notable changes to TimecodeSyncPlayer are documented in this file.

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

- While video is playing, requesting the entire UI Automation tree can be delayed or fail even
  when direct access to individual controls remains available. UI Automation responsiveness during
  playback is planned for improvement.
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
