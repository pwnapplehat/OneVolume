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
    bool Gated);

/// <summary>
/// The heart of OneVolume: a deterministic, side-effect-free-except-volume control loop.
/// Call <see cref="Tick"/> at a steady cadence (the app uses 20 Hz); each tick it
///   1. reads every session's pre-volume peak,
///   2. maintains a smoothed loudness estimate per session,
///   3. steers session volume so heard level (raw × volume) approaches the target —
///      attenuation-only, ramped, with a deadband, a silence gate, a blast clamp,
///      and per-app exclusions,
///   4. remembers each session's original volume and restores it on demand
///      (turning OneVolume off must leave the system exactly as found).
/// The engine holds no Windows handles itself — it works against IAudioSessionSource,
/// which makes every rule above unit-testable with fake sessions.
/// </summary>
public sealed class LevelingEngine
{
    private readonly IAudioSessionSource _source;
    private readonly LevelerSettings _settings;
    private readonly Dictionary<string, float> _smoothed = new();
    private readonly Dictionary<string, float> _originalVolume = new();
    private readonly HashSet<string> _correcting = new();

    public LevelingEngine(IAudioSessionSource source, LevelerSettings settings)
    {
        _source = source;
        _settings = settings;
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

            // First sight of this session: remember the user's own volume for restore.
            if (!_originalVolume.ContainsKey(session.Id))
            {
                _originalVolume[session.Id] = volume;
            }

            bool excluded = _settings.ExcludedProcesses.Contains(session.ProcessName);

            // Smoothed pre-volume loudness estimate.
            float previous = _smoothed.TryGetValue(session.Id, out float s) ? s : raw;
            float smoothedNow = previous + _settings.MeterSmoothing * (raw - previous);
            _smoothed[session.Id] = smoothedNow;

            bool gated = smoothedNow < _settings.NoiseGate;
            float heard = smoothedNow * volume;

            if (!excluded && !gated)
            {
                bool isBlast = raw * volume > blastLevel;
                if (isBlast)
                {
                    // Something suddenly screamed well past the target: clamp fast using the
                    // INSTANT peak (the smoothed estimate lags exactly when it matters most).
                    _correcting.Add(session.Id);
                    float desired = Math.Clamp(target / Math.Max(raw, 1e-4f), _settings.MinVolume, 1f);
                    float step = Math.Clamp(desired - volume, -_settings.BlastStep, 0f);
                    if (step < 0)
                    {
                        TrySetVolume(session, volume + step);
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
                            if (Math.Abs(step) > 0.0005f)
                            {
                                TrySetVolume(session, volume + step);
                                volume += step;
                            }
                        }
                    }
                }
            }

            states.Add(new SessionState(
                session.Id, session.ProcessId, session.ProcessName,
                raw, smoothedNow, volume, smoothedNow * volume, excluded, gated));
        }

        // Forget sessions that no longer exist so their state can't leak onto reused ids.
        PruneDead(seen);
        LastStates = states;
    }

    /// <summary>
    /// Puts every session the engine ever adjusted back to the volume the user had set
    /// before OneVolume touched it. Called when leveling is turned off or the app exits.
    /// </summary>
    public void RestoreOriginalVolumes()
    {
        IReadOnlyList<IAudioSession> sessions = _source.GetSessions();
        foreach (IAudioSession session in sessions)
        {
            if (session.IsAlive && _originalVolume.TryGetValue(session.Id, out float original))
            {
                TrySetVolume(session, original);
            }
        }

        _originalVolume.Clear();
        _smoothed.Clear();
        _correcting.Clear();
    }

    private static void TrySetVolume(IAudioSession session, float value)
    {
        try
        {
            session.Volume = Math.Clamp(value, 0f, 1f);
        }
        catch
        {
            // Session vanished mid-write — the next tick prunes it.
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

        _correcting.RemoveWhere(k => !alive.Contains(k));
    }
}
