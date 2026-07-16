using System.Text.Json;

namespace OneVolume.Core.Leveling;

/// <summary>
/// Crash-safe record of every session's true original volume, persisted to disk the moment
/// the engine first sees (and may start adjusting) a session.
///
/// Why it exists: Windows itself remembers per-app volumes across app restarts. If
/// OneVolume is force-killed or the machine crashes while an app is attenuated, that
/// attenuated value would silently become the app's "normal" volume — and a fresh engine
/// would adopt it as the original, losing the user's real setting forever. On startup the
/// app replays leftover journal entries onto matching live sessions (see
/// <see cref="LevelingEngine.RecoverOrphanedVolumes"/>), then the journal is cleared and
/// rebuilt as leveling proceeds. A clean restore also clears it — an empty journal means
/// "nothing is being managed right now".
///
/// Keyed by <see cref="Audio.IAudioSession.StableId"/> (device + app session identifier,
/// no PID) — the same identity Windows uses to persist per-app volume, so entries survive
/// app restarts. Corrupt or missing files are treated as empty; journaling failures never
/// break leveling.
/// </summary>
public sealed class VolumeJournal
{
    private const int MaxEntries = 512;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly string _filePath;
    private readonly Dictionary<string, Entry> _entries = new();

    private sealed record Entry(float Volume, DateTime SeenUtc);

    private sealed class Model
    {
        public Dictionary<string, float> Volumes { get; set; } = [];
        public Dictionary<string, DateTime> Seen { get; set; } = [];
    }

    public VolumeJournal(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneVolume", "volume-journal.json");
        Load();
    }

    /// <summary>Number of remembered originals (for tests/diagnostics).</summary>
    public int Count => _entries.Count;

    /// <summary>The journaled original volume for a session identity, if any.</summary>
    public bool TryGet(string stableId, out float volume)
    {
        if (_entries.TryGetValue(stableId, out Entry? entry))
        {
            volume = entry.Volume;
            return true;
        }

        volume = 0f;
        return false;
    }

    /// <summary>Records (or overwrites) the original volume for a session identity.</summary>
    public void Record(string stableId, float volume)
    {
        _entries[stableId] = new Entry(volume, DateTime.UtcNow);
        Save();
    }

    /// <summary>All current entries — used by startup recovery.</summary>
    public IReadOnlyDictionary<string, float> Snapshot()
        => _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Volume);

    /// <summary>
    /// Removes one settled entry (its session was restored or re-adopted). Entries for
    /// apps that are NOT currently running are deliberately kept — they heal the app the
    /// next time it appears.
    /// </summary>
    public void Remove(string stableId)
    {
        if (_entries.Remove(stableId))
        {
            Save();
        }
    }

    /// <summary>Wipes the journal.</summary>
    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            Model? model = JsonSerializer.Deserialize<Model>(File.ReadAllText(_filePath));
            if (model is null)
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - MaxAge;
            foreach ((string key, float volume) in model.Volumes)
            {
                DateTime seen = model.Seen.TryGetValue(key, out DateTime t) ? t : DateTime.UtcNow;
                if (seen >= cutoff && volume is >= 0f and <= 1f)
                {
                    _entries[key] = new Entry(volume, seen);
                }
            }

            // Cap: keep the most recently seen entries only.
            if (_entries.Count > MaxEntries)
            {
                foreach (string key in _entries.OrderByDescending(kv => kv.Value.SeenUtc)
                             .Skip(MaxEntries).Select(kv => kv.Key).ToList())
                {
                    _entries.Remove(key);
                }
            }
        }
        catch
        {
            _entries.Clear(); // corrupt journal = empty journal
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var model = new Model
            {
                Volumes = _entries.ToDictionary(kv => kv.Key, kv => kv.Value.Volume),
                Seen = _entries.ToDictionary(kv => kv.Key, kv => kv.Value.SeenUtc),
            };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(model));
        }
        catch
        {
            // Journaling is best-effort; leveling must continue regardless.
        }
    }
}
