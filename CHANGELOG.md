# Changelog

All notable changes to OneVolume are documented here.

## 1.0.1 — 2026-07-17

Full line-by-line audit of 1.0.0 with real-hardware verification of every fix.

### Fixed

- **Engine fought manual volume changes.** Dragging an app's slider in the Windows
  volume mixer while leveling was active made the engine steer it right back. Sessions
  whose volume is changed externally are now **pinned**: the user's choice wins until the
  app closes or leveling is toggled, and pause/exit restores *their* value, not the
  pre-engine one. Verified on hardware: a mid-leveling manual change to 90% stays at 90%.
- **A crash could permanently rewrite per-app volumes.** Windows persists session volumes
  across app restarts, so a force-kill/power loss while an app was attenuated silently
  made that attenuation the app's new normal (reproduced on hardware). Originals are now
  journaled to `%LocalAppData%\OneVolume\volume-journal.json` the moment leveling first
  touches a session; startup (and leveling resume) heals any leftover entries. The same
  journal also fixes apps closed-and-reopened mid-leveling adopting an attenuated volume
  as their "original".
- **Possible crash with multi-session apps.** The live list keyed rows by process id;
  an app with two audio sessions (some browsers, players) could create duplicate rows and
  crash the next refresh. Rows are now keyed by session id.
- **Second launch appeared to do nothing.** Launching OneVolume.exe while it was already
  running exited silently; it now signals the running instance to show its window.
- **Session list showed stale rows while paused**, and every session was labeled
  "leveled" even when untouched. Statuses are now honest: `leveling`, `steady`, `manual`,
  `silent`, `excluded`; the list clears while paused.
- Exclusion-list parsing had dead code and no dedup; it now lives in
  `ProcessNames.Parse` (trimmed, `.exe` stripped, case-insensitive dedup) with tests.
- Failed volume writes (session died mid-write) could poison the override detector with
  values that were never applied; writes are now confirmed before being recorded.
- Wrong copyright holder in LICENSE (copy-paste from a sibling project).

### Changed

- Default-device re-resolution is throttled to once per second instead of 20× per second
  (session refresh remains at 20 Hz). Measured: ≈0.0–0.5% CPU, flat memory, flat handles
  over extended runs.
- The E2E harness now also verifies restore-exactness on live sessions, and a second
  harness covers crash recovery + manual-override pinning end to end.
- Device-name change notifications only fire when the default output actually switches.

## 1.0.0 — 2026-07-16

Initial release.

- Automatic per-app loudness leveling via WASAPI session APIs (no drivers, no admin,
  no audio processing, zero network code).
- Attenuation-only steering toward a user target with deadband + hysteresis, sub-second
  blast clamping, silence gating, per-app exclusions, and night mode.
- Exact volume restoration on pause/exit.
- WPF Fluent tray app with live session meters; portable single-exe distribution.
- 10 deterministic engine tests; real-hardware E2E (16.9 dB gap → 0.1 dB).
