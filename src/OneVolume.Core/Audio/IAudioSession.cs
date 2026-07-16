namespace OneVolume.Core.Audio;

/// <summary>
/// One app's audio session on the output device — the only surface the engine touches.
/// Volume is the per-session (per-app) volume, exactly what the Windows volume mixer shows.
/// RawPeak is the session meter, which reports the app's rendered signal BEFORE the session
/// volume is applied (verified empirically in the spike) — so what the user hears is
/// RawPeak × Volume.
/// </summary>
public interface IAudioSession
{
    /// <summary>Stable identity for this session while it lives (device + session instance).</summary>
    string Id { get; }

    /// <summary>
    /// Identity that survives process restarts (device + app session identifier, no PID).
    /// Windows persists per-app volumes under this identity, which is what makes crash
    /// recovery possible: after a force-kill we can match a live session back to the
    /// original volume we journaled for it.
    /// </summary>
    string StableId { get; }

    int ProcessId { get; }

    string ProcessName { get; }

    /// <summary>Pre-volume peak level of the app's audio, 0..1.</summary>
    float RawPeak { get; }

    /// <summary>Per-app session volume, 0..1 (the mixer slider).</summary>
    float Volume { get; set; }

    /// <summary>False once the session/process has gone away — the engine then forgets it.</summary>
    bool IsAlive { get; }
}

/// <summary>Provides the current set of app sessions on the default output device.</summary>
public interface IAudioSessionSource : IDisposable
{
    /// <summary>Human-readable name of the output device the sessions belong to.</summary>
    string DeviceName { get; }

    /// <summary>Snapshot of the sessions that currently exist (refreshed on each call).</summary>
    IReadOnlyList<IAudioSession> GetSessions();
}
