# Changelog

All notable changes to TimecodeSyncPlayer are documented in this file.

## 0.2.0 - 2026-07-16

### Added

- Added the `showDebugOsd` setting for opt-in playback time and media metadata overlays.
- Added selectable full-screen video output for connected external displays.
- Added the `fullscreenDisplayDeviceName` setting to restore the selected output display.

### Fixed

- Double-clicking the playlist scrollbar or empty area no longer starts playback of the selected track.

### Changed

- Debug OSD is hidden by default while seek feedback via the mpv OSD bar remains available.
- Split the README into dedicated Japanese and English editions with streamlined release and setup guidance.
- Enabled PerMonitorV2 DPI awareness for sharp, correctly positioned output across displays with different scaling.

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
