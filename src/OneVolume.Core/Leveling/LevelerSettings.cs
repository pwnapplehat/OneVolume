namespace OneVolume.Core.Leveling;

/// <summary>
/// User-facing knobs for the leveling engine. Everything has a conservative default so the
/// out-of-box experience is "turn it on and forget it".
/// </summary>
public sealed class LevelerSettings
{
    /// <summary>
    /// The loudness every app is steered toward, as a "heard" peak level 0..1.
    /// Attenuation-only: apps quieter than the target are left at full session volume
    /// (we never boost past 100%, which would distort); apps louder are turned down.
    /// 0.25 ≈ comfortable media level with headroom.
    /// </summary>
    public float TargetLevel { get; set; } = 0.25f;

    /// <summary>
    /// Tolerance band (in dB) around the target inside which we do not adjust at all —
    /// prevents constant micro-jitter of app volumes ("volume seasickness").
    /// </summary>
    public double DeadbandDb { get; set; } = 3.0;

    /// <summary>
    /// Signals below this raw level are treated as silence and never acted on
    /// (silence must not make the engine crank an app to 100% and then blast).
    /// </summary>
    public float NoiseGate { get; set; } = 0.01f;

    /// <summary>
    /// Per-tick smoothing factor 0..1 for the loudness estimate (exponential moving
    /// average; at 20 Hz with 0.15 the time constant is ≈ 0.3 s). Higher = reacts
    /// faster but pumps more.
    /// </summary>
    public float MeterSmoothing { get; set; } = 0.15f;

    /// <summary>
    /// Maximum volume change per tick (0..1 scale). Small = smooth, imperceptible ramps;
    /// blasts are additionally capped by <see cref="BlastStep"/>.
    /// </summary>
    public float MaxStep { get; set; } = 0.04f;

    /// <summary>
    /// Emergency step used when a session suddenly exceeds the target by
    /// <see cref="BlastThresholdDb"/> (an ad screaming at 2 AM) — clamps down fast.
    /// </summary>
    public float BlastStep { get; set; } = 0.25f;

    /// <summary>How far above target (dB) counts as a blast needing the fast clamp.</summary>
    public double BlastThresholdDb { get; set; } = 9.0;

    /// <summary>Sessions are never attenuated below this floor, so nothing goes fully silent.</summary>
    public float MinVolume { get; set; } = 0.05f;

    /// <summary>
    /// Night mode: pulls the target down and tightens the range so late-night listening
    /// stays flat and quiet.
    /// </summary>
    public bool NightMode { get; set; }

    /// <summary>Target used while night mode is on.</summary>
    public float NightTargetLevel { get; set; } = 0.12f;

    /// <summary>Deadband used while night mode is on (tighter than daytime).</summary>
    public double NightDeadbandDb { get; set; } = 1.5;

    /// <summary>
    /// Process names (case-insensitive, no extension) the engine must never touch —
    /// e.g. games, DAWs, screen readers. Communications apps are governed separately
    /// by Windows' own ducking; users can add them here too.
    /// Kept for backward compatibility; new code should add an <see cref="AppRule"/>
    /// with <see cref="RuleKind.Exclude"/> instead. The engine honors both.
    /// </summary>
    public HashSet<string> ExcludedProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-app rules keyed by process name. A rule beats the default behavior:
    /// Exclude = never touch, Fixed = set to a chosen volume when the app appears,
    /// Level = explicit default (useful to override nothing, but allowed).
    /// </summary>
    public Dictionary<string, AppRule> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The effective rule kind for a process (legacy exclusions honored).</summary>
    public AppRule ResolveRule(string processName)
    {
        if (Rules.TryGetValue(processName, out AppRule? rule))
        {
            return rule;
        }

        return ExcludedProcesses.Contains(processName)
            ? new AppRule(processName, RuleKind.Exclude)
            : new AppRule(processName, RuleKind.Level);
    }

    /// <summary>Effective target for the current mode.</summary>
    public float EffectiveTarget => NightMode ? NightTargetLevel : TargetLevel;

    /// <summary>Effective deadband for the current mode.</summary>
    public double EffectiveDeadbandDb => NightMode ? NightDeadbandDb : DeadbandDb;

    // ---------------------------------------------------------------- LUFS path

    /// <summary>
    /// Steer using true perceived loudness (BS.1770 momentary LUFS from per-process
    /// capture) whenever a loudness provider can measure the app; peak metering remains
    /// the always-available fallback (and the blast clamp stays peak-based — it must
    /// react faster than any 400 ms loudness window can).
    /// </summary>
    public bool UseLufs { get; set; } = true;

    /// <summary>
    /// Typical content peaks this many dB above its average loudness (crest factor).
    /// Mapping the user's peak-domain target to LUFS subtracts this allowance so real
    /// music/video lands at a comparable perceived level to what the peak path produced.
    /// </summary>
    public double CrestAllowanceDb { get; set; } = 6.0;

    /// <summary>
    /// The LUFS the current target maps to: a sine peaking at the target level measures
    /// ≈ 20·log10(t) LUFS (the standard's −0.691 offset cancels the K-filter gain at
    /// 997 Hz); real content sits ~crest allowance below its peaks. Night mode flows
    /// through automatically via EffectiveTarget.
    /// </summary>
    public double TargetLufs => 20.0 * Math.Log10(Math.Max(EffectiveTarget, 1e-4)) - CrestAllowanceDb;

    /// <summary>Momentary loudness below this is silence for the LUFS path.</summary>
    public double LufsGate { get; set; } = -45.0;

    /// <summary>Max volume change per tick on the LUFS path, in dB (0.5 dB @ 20 Hz = 10 dB/s).</summary>
    public double MaxStepDbLufs { get; set; } = 0.5;
}
