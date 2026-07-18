# Changelog

All notable changes to OneVolume are documented here.

## 1.2.0 — 2026-07-18

### Changed — leveling now uses true perceived loudness

- **BS.1770 LUFS steering.** Each steered app is measured with a per-process loopback
  tap (Windows 10 2004+; read-only, post-session-volume — verified empirically to be
  exactly what you hear) feeding a streaming ITU-R BS.1770-4 K-weighted momentary meter
  (400 ms window). The engine steers in the dB domain toward the target's LUFS mapping.
  This is the same loudness standard YouTube/Spotify/Netflix normalize with, and it
  fixes the honest weakness of peak metering: a heavily compressed ad and a dynamic
  movie with identical peaks differ hugely in how loud they *sound*. K-weighting also
  stops bass-heavy content from being over-attenuated (sub-bass is discounted ≈11 dB;
  a peak meter can't tell 25 Hz rumble from 1 kHz dialogue).
- **Quiet scenes ride back up.** On the loudness path the volume recovers (never past
  100% — attenuation-only still holds) when content gets quieter, instead of staying
  parked at the level the loudest scene forced.
- **Peak fallback everywhere it matters.** Capture unavailable (older Windows, exotic
  sessions) → the 1.1 peak path takes over per-app, automatically. Blast protection
  deliberately stays peak-based: a 400 ms loudness window cannot clamp a sudden scream;
  the instantaneous peak can.

### Fixed
- A transient WASAPI enumeration miss (COM hiccup, device re-resolution) could make the
  engine forget a live session's true original volume and re-adopt the attenuated one as
  "original", corrupting the restore point. Session state now survives short enumeration
  gaps (2 s grace before pruning). Found by the E2E harness during this release.

### Verified
- 54 unit tests (new: BS.1770 math vs analytic sine values at 48/44.1 kHz, K-weighting
  frequency behavior, streaming-window semantics, LUFS steering convergence/no-boost/
  quiet-scene recovery/gating, peak fallback, peak-based blast with LUFS active,
  capture-hub tracking, prune-grace regression).
- Spike proved on hardware first: per-process capture isolates the target app's audio
  at 74.8 dB from a decoy app; measured LUFS within 0.03 dB of the analytic value;
  the tap is post-session-volume.
- New hardware E2E (`e2e-lufs`): two tones 9.5 dB apart converge to the same perceived
  loudness (gap 0.27 dB) through real capture; existing peak and rules E2Es still pass.
- Perf with capture threads active: ≈0.1–1% CPU, flat memory, flat handles.

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
