using OneVolume.Core.Audio;
using OneVolume.Core.Leveling;
using Xunit;

namespace OneVolume.Core.Tests;

/// <summary>Deterministic fake session — lets every engine rule be tested without audio hardware.</summary>
internal sealed class FakeSession : IAudioSession
{
    public FakeSession(string id, string processName, float rawPeak)
    {
        Id = id;
        ProcessName = processName;
        RawPeak = rawPeak;
        ProcessId = Math.Abs(id.GetHashCode());
    }

    public string Id { get; }
    public int ProcessId { get; }
    public string ProcessName { get; }
    public float RawPeak { get; set; }
    public float Volume { get; set; } = 1.0f;
    public bool IsAlive { get; set; } = true;

    /// <summary>What a user would hear from this app right now.</summary>
    public float Heard => RawPeak * Volume;
}

internal sealed class FakeSource : IAudioSessionSource
{
    public List<FakeSession> Sessions { get; } = [];
    public string DeviceName => "Fake Device";
    public IReadOnlyList<IAudioSession> GetSessions() => Sessions.Where(s => s.IsAlive).Cast<IAudioSession>().ToList();
    public void Dispose() { }
}

public class LevelingEngineTests
{
    private static (LevelingEngine Engine, FakeSource Source, LevelerSettings Settings) Make()
    {
        var source = new FakeSource();
        var settings = new LevelerSettings();
        return (new LevelingEngine(source, settings), source, settings);
    }

    private static void RunTicks(LevelingEngine engine, int count)
    {
        for (int i = 0; i < count; i++)
        {
            engine.Tick();
        }
    }

    [Fact]
    public void Loud_and_quiet_apps_converge_to_the_same_heard_level()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        var loud = new FakeSession("a", "video", rawPeak: 0.8f);
        var quiet = new FakeSession("b", "music", rawPeak: 0.10f);
        source.Sessions.AddRange([loud, quiet]);

        RunTicks(engine, 200);

        // Loud app pulled down to ~target; quiet app left near full volume (attenuation-only).
        double gapDb = 20 * Math.Abs(Math.Log10(loud.Heard / Math.Max(quiet.Heard, 1e-6)));
        Assert.True(loud.Volume < 0.5f, $"loud app should be attenuated, was {loud.Volume}");
        Assert.True(quiet.Volume > 0.95f, $"quiet app should stay near 1.0, was {quiet.Volume}");
        Assert.True(loud.Heard <= settings.TargetLevel * 1.5f, $"loud app heard {loud.Heard} should be near target {settings.TargetLevel}");
        Assert.True(gapDb < 9, $"apps should be much closer than the original 18 dB, gap was {gapDb:0.0} dB");
    }

    [Fact]
    public void Never_boosts_a_session_above_full_volume()
    {
        (LevelingEngine engine, FakeSource source, _) = Make();
        var faint = new FakeSession("a", "podcast", rawPeak: 0.02f);
        source.Sessions.Add(faint);

        RunTicks(engine, 100);

        Assert.True(faint.Volume <= 1.0f);
    }

    [Fact]
    public void Silence_is_gated_and_never_adjusted()
    {
        (LevelingEngine engine, FakeSource source, _) = Make();
        var silent = new FakeSession("a", "idleapp", rawPeak: 0.0f) { Volume = 0.6f };
        source.Sessions.Add(silent);

        RunTicks(engine, 100);

        Assert.Equal(0.6f, silent.Volume, 3); // untouched
        Assert.True(engine.LastStates.Single().Gated);
    }

    [Fact]
    public void Excluded_processes_are_never_touched()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        settings.ExcludedProcesses.Add("mygame");
        var game = new FakeSession("a", "MyGame", rawPeak: 0.9f) { Volume = 0.8f };
        source.Sessions.Add(game);

        RunTicks(engine, 100);

        Assert.Equal(0.8f, game.Volume, 3);
        Assert.True(engine.LastStates.Single().Excluded);
    }

    [Fact]
    public void Within_deadband_no_adjustment_happens()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        // Heard level exactly at target: inside the deadband.
        var steady = new FakeSession("a", "app", rawPeak: settings.TargetLevel) { Volume = 1.0f };
        source.Sessions.Add(steady);

        RunTicks(engine, 50);

        Assert.Equal(1.0f, steady.Volume, 3);
    }

    [Fact]
    public void Sudden_blast_is_clamped_much_faster_than_normal_ramp()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        var app = new FakeSession("a", "browser", rawPeak: settings.TargetLevel);
        source.Sessions.Add(app);
        RunTicks(engine, 30); // settle at steady state

        app.RawPeak = 1.0f;   // the 2 AM ad
        engine.Tick();
        engine.Tick();

        // Two ticks with BlastStep 0.25 must already have cut far more than two
        // normal MaxStep (0.04) ticks could.
        Assert.True(app.Volume < 1.0f - 2 * settings.MaxStep,
            $"blast clamp should outpace normal ramp, volume was {app.Volume}");

        RunTicks(engine, 100);
        Assert.True(app.Heard <= settings.TargetLevel * 1.6f, $"blast should settle near target, heard {app.Heard}");
    }

    [Fact]
    public void Restore_puts_volumes_back_exactly_as_found()
    {
        (LevelingEngine engine, FakeSource source, _) = Make();
        var loud = new FakeSession("a", "video", rawPeak: 0.9f) { Volume = 0.77f };
        source.Sessions.Add(loud);

        RunTicks(engine, 150);
        Assert.NotEqual(0.77f, loud.Volume); // engine changed it

        engine.RestoreOriginalVolumes();
        Assert.Equal(0.77f, loud.Volume, 3); // and put it back
    }

    [Fact]
    public void Night_mode_levels_to_a_lower_target()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        settings.NightMode = true;
        var app = new FakeSession("a", "video", rawPeak: 0.8f);
        source.Sessions.Add(app);

        RunTicks(engine, 300);

        Assert.True(app.Heard <= settings.NightTargetLevel * 1.6f,
            $"night mode should hold near {settings.NightTargetLevel}, heard {app.Heard}");
    }

    [Fact]
    public void Dead_sessions_are_pruned_and_do_not_leak_state()
    {
        (LevelingEngine engine, FakeSource source, _) = Make();
        var app = new FakeSession("a", "video", rawPeak: 0.9f);
        source.Sessions.Add(app);
        RunTicks(engine, 50);

        app.IsAlive = false;
        engine.Tick();
        Assert.Empty(engine.LastStates);

        // Same id re-appears (session ids can be reused after an app restarts): the engine
        // must treat it as brand new — including capturing its fresh original volume.
        var reborn = new FakeSession("a", "video", rawPeak: 0.9f) { Volume = 0.5f };
        source.Sessions.Clear();
        source.Sessions.Add(reborn);
        RunTicks(engine, 5);
        engine.RestoreOriginalVolumes();
        Assert.Equal(0.5f, reborn.Volume, 2);
    }

    [Fact]
    public void Volume_changes_are_ramped_never_jumping()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        var loud = new FakeSession("a", "video", rawPeak: 0.9f);
        source.Sessions.Add(loud);

        float previous = loud.Volume;
        for (int i = 0; i < 60; i++)
        {
            engine.Tick();
            float delta = Math.Abs(loud.Volume - previous);
            Assert.True(delta <= settings.BlastStep + 0.001f, $"step {delta} exceeded max allowed");
            previous = loud.Volume;
        }
    }
}
