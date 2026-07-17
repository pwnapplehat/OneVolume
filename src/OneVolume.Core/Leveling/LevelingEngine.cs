using OneVolume.Core.Audio;

namespace OneVolume.Core.Leveling;

/// <summary>What the engine decided for one session on one tick (for UI/diagnostics).</summary>
public sealed record SessionState(
    string Id,
    int ProcessId,
    string ProcessName,
    float RawPeak,
    float SmoothedPeak,
    float Volume,
    float HeardLevel,
    bool Excluded,
    bool Gated,
    bool Pinned,
    bool Correcting);

/// <summary>
/// The heart of OneVolume: a deterministic, side-effect-free-except-volume control loop.
/// Call <see cref="Tick"/> at a steady cadence (the app uses 20 Hz); each tick it
///   1. reads every session's pre-volume peak,
///   2. maintains a smoothed loudness estimate per session,
///   3. steers session volume so heard level (raw × volume) approaches the target —
///      attenuation-only, ramped, with a deadband, a silence gate, a blast clamp,
///      and per-app exclusions,
///   4. respects the user: if a session's volume was changed by anyone else (the Windows
///      volume mixer, the app itself), that session is PINNED — the user's explicit choice
///      wins over the algorithm until the session ends or leveling is toggled off/on,
///   5. remembers each session's original volume and restores it on demand
///      (turning OneVolume off must leave the system exactly as found).
/// The engine holds no Windows handles itself — it works against IAudioSessionSource,
/// which makes every rule above unit-testable with fake sessions.
/// </summary>
public sealed class LevelingEngine
{
    /// <summary>
    /// Tolerance when comparing a session's current volume against the value we last wrote.
    /// WASAPI stores session volume as a float and round-trips our writes closely; anything
    /// beyond this is a deliberate external change, not representation noise.
    /// </summary>
    private const float ExternalChangeEpsilon = 0.015f;

    private readonly IAudioSessionSource _source;
    private readonly LevelerSettings _settings;
    private readonly VolumeJournal? _journal;
    private readonly Dictionary<string, float> _smoothed = new();
    private readonly Dictionary<string, float> _originalVolume = new();
    private readonly Dictionary<string, float> _lastSetVolume = new();
    private readonly HashSet<string> _pinned = new();
    private readonly HashSet<string> _correcting = new();
    private readonly HashSet<string> _fixedApplied = new();

    public LevelingEngine(IAudioSessionSource source, LevelerSettings settings, VolumeJournal? journal = null)
    {
        _source = source;
        _settings = settings;
        _journal = journal;
    }

    /// <summary>Latest per-session decisions, refreshed by each <see cref="Tick"/>.</summary>
    public IReadOnlyList<SessionState> LastStates { get; private set; } = [];

    /// <summary>Runs one control step. Safe to call from a timer; never throws for a dead session.</summary>
    public void Tick()
    {
        IReadOnlyList<IAudioSession> sessions = _source.GetSessions();
        var states = new List<SessionState>(sessions.Count);
        var seen = new HashSet<string>();

        float target = _settings.EffectiveTarget;
        double deadbandDb = _settings.EffectiveDeadbandDb;
        float deadbandHigh = target * (float)Math.Pow(10, deadbandDb / 20.0);
        float deadbandLow = target / (float)Math.Pow(10, deadbandDb / 20.0);
        float blastLevel = target * (float)Math.Pow(10, _settings.BlastThresholdDb / 20.0);

        foreach (IAudioSession session in sessions)
        {
            if (!session.IsAlive)
            {
                continue;
            }

            seen.Add(session.Id);

            float raw;
            float volume;
            try
            {
                raw = session.RawPeak;
                volume = session.Volume;
            }
            catch
            {
                continue; // session died between snapshot and read
            }

            // First sight of this session: remember the user's own volume for restore,
            // and journal it to disk so a crash can never make an attenuated volume
            // permanent (Windows persists per-app volumes across app restarts).
            //
            // If the journal already knows this app (same StableId) from the current
            // leveling era, the app was closed and reopened while we were attenuating it —
            // Windows re-applied the attenuated volume, so the CURRENT volume is ours,
            // not the user's. Keep the journaled true original as the restore point.
            if (!_originalVolume.ContainsKey(session.Id))
            {
                if (_journal is not null && _journal.TryGet(session.StableId, out float journaled))
                {
                    _originalVolume[session.Id] = journaled;
                }
                else
                {
                    _originalVolume[session.Id] = volume;
                    _journal?.Record(session.StableId, volume);
                }
            }

            // User override detection: if the volume moved since WE last set it, someone
            // else changed it deliberately (volume mixer, the app itself). Their choice
            // wins — pin the session (stop leveling it) and make their value the new
            // restore point so pause/exit doesn't undo an explicit user action.
            if (_lastSetVolume.TryGetValue(session.Id, out float lastSet)
                && Math.Abs(volume - lastSet) > ExternalChangeEpsilon)
            {
                _pinned.Add(session.Id);
                _correcting.Remove(session.Id);
                _originalVolume[session.Id] = volume;
                _journal?.Record(session.StableId, volume);
            }

            AppRule rule = _settings.ResolveRule(session.ProcessName);
            bool excluded = rule.Kind == RuleKind.Exclude;

            // Fixed rule: set the app to its configured volume once, when the session
            // first appears, then pin it — the app holds that volume (and a later manual
            // change by the user wins, exactly like any other pin). The pre-rule volume
            // was already captured above, but the rule IS the user's declared intent, so
            // the restore point becomes the fixed value.
            if (rule.Kind == RuleKind.Fixed && _fixedApplied.Add(session.Id))
            {
                float fixedVolume = rule.SafeFixedVolume;
                if (TrySetVolume(session, fixedVolume))
                {
                    volume = fixedVolume;
                    _pinned.Add(session.Id);
                    _correcting.Remove(session.Id);
                    _originalVolume[session.Id] = fixedVolume;
                    _lastSetVolume[session.Id] = fixedVolume;
                    _journal?.Record(session.StableId, fixedVolume);
                }
                else
                {
                    _fixedApplied.Remove(session.Id); // write failed — retry next tick
                }
            }

            bool pinned = _pinned.Contains(session.Id);

            // Smoothed pre-volume loudness estimate.
            float previous = _smoothed.TryGetValue(session.Id, out float s) ? s : raw;
            float smoothedNow = previous + _settings.MeterSmoothing * (raw - previous);
            _smoothed[session.Id] = smoothedNow;

            bool gated = smoothedNow < _settings.NoiseGate;
            float heard = smoothedNow * volume;

            if (!excluded && !pinned && !gated)
            {
                bool isBlast = raw * volume > blastLevel;
                if (isBlast)
                {
                    // Something suddenly screamed well past the target: clamp fast using the
                    // INSTANT peak (the smoothed estimate lags exactly when it matters most).
                    _correcting.Add(session.Id);
                    float desired = Math.Clamp(target / Math.Max(raw, 1e-4f), _settings.MinVolume, 1f);
                    float step = Math.Clamp(desired - volume, -_settings.BlastStep, 0f);
                    if (step < 0 && TrySetVolume(session, volume + step))
                    {
                        volume += step;
                    }
                }
                else
                {
                    // Hysteresis: leaving the deadband STARTS a correction; once correcting,
                    // steer all the way to the target centre (±0.5 dB) before resting again.
                    // A plain deadband would park loud apps at the band's edge instead of
                    // the target, wasting most of the tolerance.
                    if (heard > deadbandHigh || heard < deadbandLow)
                    {
                        _correcting.Add(session.Id);
                    }

                    if (_correcting.Contains(session.Id))
                    {
                        float settleHigh = target * 1.06f; // ≈ +0.5 dB
                        float settleLow = target * 0.94f;  // ≈ −0.5 dB

                        bool settled = heard >= settleLow && heard <= settleHigh;
                        bool unreachable = volume >= 0.999f && heard < settleLow; // quiet app: can't boost past 1.0
                        if (settled || unreachable)
                        {
                            _correcting.Remove(session.Id);
                        }
                        else
                        {
                            float desired = Math.Clamp(target / Math.Max(smoothedNow, 1e-4f), _settings.MinVolume, 1f);
                            float step = Math.Clamp(desired - volume, -_settings.MaxStep, _settings.MaxStep);
                            if (Math.Abs(step) > 0.0005f && TrySetVolume(session, volume + step))
                            {
                                volume += step;
                            }
                        }
                    }
                }
            }

            // Record what the volume is as WE leave it this tick — the baseline for
            // detecting an external (user) change on the next tick.
            _lastSetVolume[session.Id] = volume;

            states.Add(new SessionState(
                session.Id, session.ProcessId, session.ProcessName,
                raw, smoothedNow, volume, smoothedNow * volume, excluded, gated,
                pinned, _correcting.Contains(session.Id)));
        }

        // Forget sessions that no longer exist so their state can't leak onto reused ids.
        PruneDead(seen);
        LastStates = states;
    }

    /// <summary>
    /// Puts every live session the engine ever adjusted back to the volume the user had
    /// set before OneVolume touched it. Called when leveling is turned off or the app
    /// exits. Journal entries are consumed only for the sessions actually restored:
    /// an app that was closed while attenuated isn't reachable here (Windows keeps its
    /// attenuated volume persisted), so its entry stays on disk and heals the app the
    /// next time it appears — at startup recovery or when it's next seen while leveling.
    /// </summary>
    public void RestoreOriginalVolumes()
    {
        IReadOnlyList<IAudioSession> sessions = _source.GetSessions();
        foreach (IAudioSession session in sessions)
        {
            if (session.IsAlive && _originalVolume.TryGetValue(session.Id, out float original)
                && TrySetVolume(session, original))
            {
                _journal?.Remove(session.StableId);
            }
        }

        _originalVolume.Clear();
        _smoothed.Clear();
        _correcting.Clear();
        _pinned.Clear();
        _lastSetVolume.Clear();
        _fixedApplied.Clear();
    }

    /// <summary>
    /// Sets one session's volume on the user's behalf (the in-app mixer slider). The
    /// session is pinned — an explicit user choice always wins over the algorithm — and
    /// the value becomes the restore point and journal entry, exactly like a change made
    /// in the Windows volume mixer. Works whether or not the engine is ticking.
    /// </summary>
    public bool SetSessionVolume(string sessionId, float volume)
    {
        float clamped = Math.Clamp(volume, 0f, 1f);
        foreach (IAudioSession session in _source.GetSessions())
        {
            if (session.Id != sessionId || !session.IsAlive)
            {
                continue;
            }

            if (!TrySetVolume(session, clamped))
            {
                return false;
            }

            _pinned.Add(sessionId);
            _correcting.Remove(sessionId);
            _originalVolume[sessionId] = clamped;
            _lastSetVolume[sessionId] = clamped;
            _journal?.Record(session.StableId, clamped);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Forgets pins and fixed-rule application for every live session of a process —
    /// called when the user edits that app's rule so the new rule takes effect
    /// immediately instead of after the app restarts.
    /// </summary>
    public void UnpinProcess(string processName)
    {
        foreach (SessionState state in LastStates)
        {
            if (string.Equals(state.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                _pinned.Remove(state.Id);
                _fixedApplied.Remove(state.Id);
                _correcting.Remove(state.Id);
            }
        }
    }

    /// <summary>
    /// Startup crash recovery: if a previous run died (force-kill, crash, power loss)
    /// while sessions were attenuated, their journaled original volumes are still on
    /// disk. Reapply them to matching live sessions and consume those entries; entries
    /// for apps that aren't running are kept so they heal on their next appearance.
    /// Returns how many sessions were fixed.
    /// </summary>
    public int RecoverOrphanedVolumes()
    {
        if (_journal is null)
        {
            return 0;
        }

        IReadOnlyDictionary<string, float> leftover = _journal.Snapshot();
        if (leftover.Count == 0)
        {
            return 0;
        }

        int recovered = 0;
        foreach (IAudioSession session in _source.GetSessions())
        {
            if (!session.IsAlive || !leftover.TryGetValue(session.StableId, out float original))
            {
                continue;
            }

            float current;
            try
            {
                current = session.Volume;
            }
            catch
            {
                continue;
            }

            if (Math.Abs(current - original) <= 0.01f || TrySetVolume(session, original))
            {
                _journal.Remove(session.StableId);
                if (Math.Abs(current - original) > 0.01f)
                {
                    recovered++;
                }
            }
        }

        return recovered;
    }

    /// <summary>
    /// Returns false when the write failed (session vanished mid-write) so callers don't
    /// record a volume that was never applied — a phantom value there would later be
    /// misread as a user override.
    /// </summary>
    private static bool TrySetVolume(IAudioSession session, float value)
    {
        try
        {
            session.Volume = Math.Clamp(value, 0f, 1f);
            return true;
        }
        catch
        {
            return false; // the next tick prunes the dead session
        }
    }

    private void PruneDead(HashSet<string> alive)
    {
        foreach (string key in _smoothed.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _smoothed.Remove(key);
        }

        foreach (string key in _originalVolume.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _originalVolume.Remove(key);
        }

        foreach (string key in _lastSetVolume.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _lastSetVolume.Remove(key);
        }

        _correcting.RemoveWhere(k => !alive.Contains(k));
        _pinned.RemoveWhere(k => !alive.Contains(k));
        _fixedApplied.RemoveWhere(k => !alive.Contains(k));
    }
}
