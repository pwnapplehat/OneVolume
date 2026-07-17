# Changelog

All notable changes to OneVolume are documented here.

## 1.1.1 — 2026-07-17

### Added
- **Notify-only update check** (same model as BitBroom Rescue): one opt-out HTTPS call
  to the GitHub releases API at startup. If a newer version exists, a banner offers
  **Download** (opens the release page), **Not now**, and **Don't check again**
  (persistent opt-out). OneVolume is a portable exe and deliberately never downloads or
  replaces itself — silent self-swaps would also re-trigger SmartScreen every release.
- README updated honestly: the app previously claimed "zero network code"; the update
  check is now the one documented network call, and how to turn it off is stated.

### Verified
- Banner flow exercised end-to-end against the live GitHub API using the
  `OV_TEST_CURRENT_VERSION` override (banner shown for 0.9.0, absent when current),
  screenshots captured through the real UI.

## 1.1.0 — 2026-07-17

OneVolume grows from "just the auto-leveler" into a full per-app volume control center.

### Added
- **Live per-app mixer.** Every playing app now has a real volume slider in the window
  (with a thin live loudness meter under it). Dragging it sets that app's volume and
  pins the session — your choice always beats the algorithm, same as using the Windows
  mixer.
- **Per-app rules — including apps that aren't running.** New "App rules" panel with an
  installed-apps browser (Start Menu scan; you can also just type a process name):
  - *Level automatically* — the default behavior;
  - *Fixed volume N%* — applied the instant the app's audio session appears, then left
    in your control (a later manual change wins for that session; pausing OneVolume
    restores the fixed value, not the pre-rule one — the rule is your declared intent);
  - *Never touch* — replaces the old comma-separated exclusion box (existing exclusion
    lists migrate automatically).
  Rule edits take effect immediately on running apps, not just after an app restart.
- Honest persistence note: Windows itself remembers per-app volumes, so a fixed volume
  usually sticks even when OneVolume isn't running; the tray app is needed for
  automatic leveling and for applying rules to newly launched apps.

### Changed
- Session status labels now include `fixed` for rule-held apps.
- Default window is slightly taller to fit the mixer and rules panels.

### Verified
- 39 unit tests (new: fixed-rule apply/hold/restore semantics, manual-change override,
  exclusion via rules, legacy migration, immediate rule re-application, mixer entry
  point with and without the engine ticking, persisted-model round-trip, clamping).
- New real-hardware E2E (`e2e-rules`): fixed rule applies on session appearance
  (0.30 exact), in-app mixer override holds (0.85), restore keeps the user's value.
- UIA smoke test: the installed-apps picker enumerates real apps (74 on the dev
  machine) through the actual UI.

## 1.0.2 — 2026-07-17

### Fixed

- App icon had opaque black corners (the rounded-square background filled the full
  canvas). Replaced with a proper transparent-corner icon across the exe, window,
  tray, and README.

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
