namespace OneVolume.Core.Leveling;

/// <summary>What OneVolume should do with a given app's audio.</summary>
public enum RuleKind
{
    /// <summary>Default: steer the app toward the global target loudness.</summary>
    Level,

    /// <summary>
    /// Hold the app at a fixed per-app volume: applied the moment the app's session
    /// appears, then left alone (a later manual change by the user wins for that
    /// session). Windows persists per-app volumes, so the value usually sticks even
    /// when OneVolume isn't running.
    /// </summary>
    Fixed,

    /// <summary>Never touch this app (games, DAWs, screen readers…).</summary>
    Exclude,
}

/// <summary>
/// A per-app rule, keyed by process name (no extension, case-insensitive). Rules can be
/// created for apps that aren't running — they apply when the app's session appears.
/// </summary>
public sealed record AppRule(string ProcessName, RuleKind Kind, float FixedVolume = 1.0f)
{
    /// <summary>Clamped fixed volume (guards hand-edited settings files).</summary>
    public float SafeFixedVolume => Math.Clamp(FixedVolume, 0.0f, 1.0f);
}
