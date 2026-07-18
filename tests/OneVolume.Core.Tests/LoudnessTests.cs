using OneVolume.Core.Leveling;
using OneVolume.Core.Loudness;

namespace OneVolume.Core.Tests;

/// <summary>
/// BS.1770 math against analytic ground truth, the streaming meter's windowing,
/// and the engine's LUFS steering (with scripted loudness) + peak fallback.
/// </summary>
public class LoudnessTests
{
    private static float[] Sine(double frequency, double amplitude, int sampleRate, int channels, double seconds)
    {
        int frames = (int)(sampleRate * seconds);
        float[] data = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            for (int c = 0; c < channels; c++)
            {
                data[i * channels + c] = v;
            }
        }

        return data;
    }

    /// <summary>
    /// Stereo 997 Hz sine at amplitude a ⇒ 20·log10(a) LUFS: per-channel mean square is
    /// a²/2·G, summed over two channels = a²·G, and the standard's −0.691 offset exists
    /// precisely to cancel the K-filter gain G at 997 Hz (10·log10 G ≈ +0.691 dB).
    /// </summary>
    private static double AnalyticSineLufs(double amplitude) => 20 * Math.Log10(amplitude);

    [Theory]
    [InlineData(0.35, 48000)]
    [InlineData(0.10, 48000)]
    [InlineData(0.35, 44100)] // coefficients must derive correctly for non-48k rates
    public void Sine_measures_at_analytic_lufs(double amplitude, int sampleRate)
    {
        float[] samples = Sine(997, amplitude, sampleRate, 2, 3.0);
        double measured = Bs1770Meter.MeasureLufs(samples, sampleRate, 2);
        Assert.InRange(measured, AnalyticSineLufs(amplitude) - 0.35, AnalyticSineLufs(amplitude) + 0.35);
    }

    [Fact]
    public void K_weighting_discounts_subbass_rumble()
    {
        // Same amplitude, 997 Hz vs 25 Hz: the RLB high-pass discounts sub-bass by
        // ≈ 11 dB at 25 Hz (reference curve) — a peak meter would call them identical.
        double mid = Bs1770Meter.MeasureLufs(Sine(997, 0.5, 48000, 2, 3.0), 48000, 2);
        double rumble = Bs1770Meter.MeasureLufs(Sine(25, 0.5, 48000, 2, 3.0), 48000, 2);
        Assert.True(mid - rumble > 9, $"expected >9 dB discount, got {mid - rumble:0.0} dB");
    }

    [Fact]
    public void K_weighting_boosts_treble_per_head_model()
    {
        double mid = Bs1770Meter.MeasureLufs(Sine(997, 0.5, 48000, 2, 3.0), 48000, 2);
        double treble = Bs1770Meter.MeasureLufs(Sine(8000, 0.5, 48000, 2, 3.0), 48000, 2);
        Assert.InRange(treble - mid, 2.0, 6.0); // shelf is ≈ +4 dB up top
    }

    [Fact]
    public void Streaming_meter_needs_a_full_window_then_matches_oneshot()
    {
        var meter = new Bs1770Meter(48000, 2);
        float[] samples = Sine(997, 0.35, 48000, 2, 1.0);

        // Feed in odd-sized chunks to exercise partial-block bookkeeping.
        meter.Process(samples.AsSpan(0, 3000));
        Assert.True(double.IsNaN(meter.MomentaryLufs), "no value before 400 ms of audio");

        int offset = 3000;
        while (offset < samples.Length)
        {
            int take = Math.Min(7777, samples.Length - offset);
            meter.Process(samples.AsSpan(offset, take));
            offset += take;
        }

        Assert.InRange(meter.MomentaryLufs, AnalyticSineLufs(0.35) - 0.5, AnalyticSineLufs(0.35) + 0.5);
    }

    [Fact]
    public void Silence_reports_negative_infinity_not_a_number_glitch()
    {
        var meter = new Bs1770Meter(48000, 2);
        meter.Process(new float[48000 * 2]); // 1 s of digital silence
        Assert.True(double.IsNegativeInfinity(meter.MomentaryLufs));
    }

    // ------------------------------------------------------------- engine + LUFS

    /// <summary>Scripted loudness: models post-volume measurement (content + 20·log10 v).</summary>
    private sealed class FakeLoudness : ILoudnessProvider
    {
        private readonly FakeSource _source;

        public FakeLoudness(FakeSource source) => _source = source;

        /// <summary>Content loudness (at volume 1.0) per process id; absent = unavailable.</summary>
        public Dictionary<int, double> ContentLufs { get; } = [];

        public HashSet<int> LastSynced { get; } = [];

        public bool TryGetMomentaryLufs(int processId, out double lufs)
        {
            lufs = double.NaN;
            if (!ContentLufs.TryGetValue(processId, out double content))
            {
                return false;
            }

            FakeSession? session = _source.Sessions.FirstOrDefault(s => s.ProcessId == processId);
            if (session is null)
            {
                return false;
            }

            lufs = content + 20 * Math.Log10(Math.Max(session.Volume, 1e-4));
            return true;
        }

        public void Sync(IReadOnlyCollection<int> processIds)
        {
            LastSynced.Clear();
            foreach (int pid in processIds)
            {
                LastSynced.Add(pid);
            }
        }
    }

    private static (LevelingEngine Engine, FakeSource Source, LevelerSettings Settings, FakeLoudness Loudness) MakeLufs()
    {
        var source = new FakeSource();
        var settings = new LevelerSettings();
        var loudness = new FakeLoudness(source);
        return (new LevelingEngine(source, settings, null, loudness), source, settings, loudness);
    }

    private static void RunTicks(LevelingEngine engine, int count)
    {
        for (int i = 0; i < count; i++)
        {
            engine.Tick();
        }
    }

    [Fact]
    public void Lufs_steering_converges_loud_app_to_target_loudness()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings, FakeLoudness loudness) = MakeLufs();
        var app = new FakeSession("a", "video", rawPeak: 0.25f); // peak says in-band — LUFS must drive anyway
        source.Sessions.Add(app);
        loudness.ContentLufs[app.ProcessId] = -8.0; // loud, compressed content

        RunTicks(engine, 300);

        double heardLufs = -8.0 + 20 * Math.Log10(app.Volume);
        Assert.True(engine.LastStates.Single().UsingLufs);
        Assert.InRange(heardLufs, settings.TargetLufs - 0.6, settings.TargetLufs + 0.6);
        Assert.True(app.Volume < 0.5f, $"loud content should be attenuated, vol={app.Volume:0.00}");
    }

    [Fact]
    public void Lufs_steering_never_boosts_quiet_content_past_full_volume()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings, FakeLoudness loudness) = MakeLufs();
        var app = new FakeSession("a", "podcast", rawPeak: 0.2f);
        source.Sessions.Add(app);
        loudness.ContentLufs[app.ProcessId] = settings.TargetLufs - 10; // 10 dB too quiet

        RunTicks(engine, 200);

        Assert.Equal(1.0f, app.Volume, 2); // attenuation-only: parked at 100%
        Assert.False(engine.LastStates.Single().Correcting, "unreachable boost must release the correction");
    }

    [Fact]
    public void Lufs_boost_recovers_own_attenuation_when_content_gets_quieter()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings, FakeLoudness loudness) = MakeLufs();
        var app = new FakeSession("a", "video", rawPeak: 0.25f);
        source.Sessions.Add(app);
        loudness.ContentLufs[app.ProcessId] = -8.0;
        RunTicks(engine, 300);
        float attenuated = app.Volume;
        Assert.True(attenuated < 0.5f);

        loudness.ContentLufs[app.ProcessId] = -14.0; // quieter scene begins (still above target)
        RunTicks(engine, 300);

        Assert.True(app.Volume > attenuated, "volume must ride back up for the quiet scene");
        double heardLufs = -14.0 + 20 * Math.Log10(app.Volume);
        Assert.InRange(heardLufs, settings.TargetLufs - 0.6, settings.TargetLufs + 0.6);
    }

    [Fact]
    public void Below_lufs_gate_nothing_is_adjusted()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings, FakeLoudness loudness) = MakeLufs();
        var app = new FakeSession("a", "idle", rawPeak: 0.05f) { Volume = 0.7f };
        source.Sessions.Add(app);
        loudness.ContentLufs[app.ProcessId] = -60.0; // below the -45 gate

        RunTicks(engine, 100);

        Assert.Equal(0.7f, app.Volume, 3);
        Assert.True(engine.LastStates.Single().Gated);
    }

    [Fact]
    public void Peak_fallback_engages_when_loudness_is_unavailable()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings, FakeLoudness loudness) = MakeLufs();
        var app = new FakeSession("a", "legacy", rawPeak: 0.9f);
        source.Sessions.Add(app);
        // No ContentLufs entry → TryGet returns false → peak path must still level.

        RunTicks(engine, 200);

        Assert.False(engine.LastStates.Single().UsingLufs);
        Assert.True(app.Heard <= settings.TargetLevel * 1.6f, $"peak fallback should level, heard {app.Heard}");
    }

    [Fact]
    public void Blast_clamp_stays_peak_based_even_with_lufs_available()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings, FakeLoudness loudness) = MakeLufs();
        var app = new FakeSession("a", "browser", rawPeak: 0.2f);
        source.Sessions.Add(app);
        loudness.ContentLufs[app.ProcessId] = settings.TargetLufs; // steady at target
        RunTicks(engine, 50);
        float before = app.Volume;

        app.RawPeak = 1.0f; // the ad screams; the 400 ms LUFS window hasn't caught up
        engine.Tick();
        engine.Tick();

        Assert.True(app.Volume < before - 2 * settings.MaxStep,
            $"peak blast clamp must fire immediately, vol {before:0.00} -> {app.Volume:0.00}");
    }

    [Fact]
    public void Hub_sync_tracks_only_steerable_sessions()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings, FakeLoudness loudness) = MakeLufs();
        settings.Rules["game"] = new AppRule("game", RuleKind.Exclude);
        var levelable = new FakeSession("a", "video", rawPeak: 0.5f);
        var excluded = new FakeSession("b", "game", rawPeak: 0.5f);
        source.Sessions.AddRange([levelable, excluded]);

        engine.Tick();

        Assert.Contains(levelable.ProcessId, loudness.LastSynced);
        Assert.DoesNotContain(excluded.ProcessId, loudness.LastSynced);
    }
}
