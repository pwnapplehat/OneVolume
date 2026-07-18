using System.Collections.Concurrent;

namespace OneVolume.Core.Loudness;

/// <summary>
/// Source of per-process perceived loudness for the engine. Abstracted so the engine's
/// LUFS steering is unit-testable with scripted values.
/// </summary>
public interface ILoudnessProvider
{
    /// <summary>
    /// Momentary (400 ms) K-weighted loudness of everything the process tree is playing,
    /// post-session-volume (what the user actually hears). False when unavailable —
    /// capture unsupported/failed or the measurement window isn't full yet.
    /// </summary>
    bool TryGetMomentaryLufs(int processId, out double lufs);

    /// <summary>Aligns the set of tracked processes with the sessions that exist now.</summary>
    void Sync(IReadOnlyCollection<int> processIds);
}

/// <summary>
/// Owns one <see cref="ProcessLoopbackCapture"/> per audible process. Captures that fail
/// to activate (pre-2004 Windows, protected processes) are remembered and not retried
/// while the process lives — the engine's peak fallback covers those. All capture work
/// happens on background threads; this class only hands out numbers.
/// NOTE: capture is per-process while volumes are per-session. For the rare app with two
/// sessions at different volumes, both sessions see the combined process loudness — the
/// engine still converges their sum to the target, which is the audibly-right outcome.
/// </summary>
public sealed class LoudnessMeterHub : ILoudnessProvider, IDisposable
{
    private readonly ConcurrentDictionary<int, ProcessLoopbackCapture> _captures = new();
    private readonly ConcurrentDictionary<int, byte> _failed = new();

    public bool TryGetMomentaryLufs(int processId, out double lufs)
    {
        lufs = double.NaN;
        if (!_captures.TryGetValue(processId, out ProcessLoopbackCapture? capture))
        {
            return false;
        }

        if (capture.Failed)
        {
            // Remember and retire — the peak fallback takes over for this process.
            if (_captures.TryRemove(processId, out ProcessLoopbackCapture? dead))
            {
                _failed[processId] = 1;
                dead.Dispose();
            }

            return false;
        }

        double value = capture.Meter.MomentaryLufs;
        if (double.IsNaN(value))
        {
            return false; // window not full yet
        }

        lufs = value;
        return true;
    }

    public void Sync(IReadOnlyCollection<int> processIds)
    {
        foreach (int pid in processIds)
        {
            if (!_captures.ContainsKey(pid) && !_failed.ContainsKey(pid))
            {
                _captures[pid] = new ProcessLoopbackCapture(pid);
            }
        }

        foreach (int tracked in _captures.Keys)
        {
            if (!processIds.Contains(tracked) && _captures.TryRemove(tracked, out ProcessLoopbackCapture? gone))
            {
                gone.Dispose();
            }
        }

        foreach (int failed in _failed.Keys)
        {
            if (!processIds.Contains(failed))
            {
                _failed.TryRemove(failed, out _); // process ended — a future launch may retry
            }
        }
    }

    public void Dispose()
    {
        foreach (ProcessLoopbackCapture capture in _captures.Values)
        {
            capture.Dispose();
        }

        _captures.Clear();
    }
}
