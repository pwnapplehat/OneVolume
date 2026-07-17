using OneVolume.Core.Leveling;
using OneVolume.Core.Settings;

namespace OneVolume.Core.Tests;

/// <summary>
/// Per-app rules (v1.1): fixed volumes applied when a session appears, exclusions via
/// rules, immediate effect when rules change, the in-app mixer entry point, and the
/// migration from the pre-1.1 exclusion list.
/// </summary>
public class AppRuleTests
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
    public void Fixed_rule_sets_volume_when_session_appears_and_holds_it()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        settings.Rules["spotify"] = new AppRule("spotify", RuleKind.Fixed, 0.40f);

        var app = new FakeSession("a", "Spotify", rawPeak: 0.9f); // loud content
        source.Sessions.Add(app);
        RunTicks(engine, 100);

        // Applied once and NOT leveled afterwards despite loud content.
        Assert.Equal(0.40f, app.Volume, 3);
        Assert.True(engine.LastStates.Single().Pinned);
    }

    [Fact]
    public void Fixed_rule_restore_point_is_the_fixed_value()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        settings.Rules["video"] = new AppRule("video", RuleKind.Fixed, 0.30f);

        var app = new FakeSession("a", "video", rawPeak: 0.8f) { Volume = 1.0f };
        source.Sessions.Add(app);
        RunTicks(engine, 20);
        engine.RestoreOriginalVolumes();

        // Pausing OneVolume must not yank the app back to the pre-rule 1.0 — the rule
        // is the user's declared intent.
        Assert.Equal(0.30f, app.Volume, 3);
    }

    [Fact]
    public void Fixed_rule_respects_a_later_manual_change()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        settings.Rules["game"] = new AppRule("game", RuleKind.Fixed, 0.50f);

        var app = new FakeSession("a", "game", rawPeak: 0.7f);
        source.Sessions.Add(app);
        RunTicks(engine, 20);
        Assert.Equal(0.50f, app.Volume, 3);

        app.Volume = 0.95f; // the user drags the Windows mixer
        RunTicks(engine, 50);

        Assert.Equal(0.95f, app.Volume, 3); // user wins for this session
    }

    [Fact]
    public void Exclude_rule_is_never_touched()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        settings.Rules["reaper"] = new AppRule("reaper", RuleKind.Exclude);

        var daw = new FakeSession("a", "REAPER", rawPeak: 0.95f) { Volume = 0.9f };
        source.Sessions.Add(daw);
        RunTicks(engine, 100);

        Assert.Equal(0.9f, daw.Volume, 3);
        Assert.True(engine.LastStates.Single().Excluded);
    }

    [Fact]
    public void Rule_edit_takes_effect_immediately_after_unpin()
    {
        (LevelingEngine engine, FakeSource source, LevelerSettings settings) = Make();
        settings.Rules["chrome"] = new AppRule("chrome", RuleKind.Fixed, 0.60f);

        var app = new FakeSession("a", "chrome", rawPeak: 0.9f);
        source.Sessions.Add(app);
        RunTicks(engine, 20);
        Assert.Equal(0.60f, app.Volume, 3);

        // User switches the rule back to automatic leveling.
        settings.Rules.Remove("chrome");
        engine.UnpinProcess("chrome");
        RunTicks(engine, 300);

        Assert.True(app.Heard <= settings.TargetLevel * 1.6f,
            $"should level again after rule removal, heard {app.Heard}");
    }

    [Fact]
    public void Mixer_set_pins_session_and_becomes_restore_point()
    {
        (LevelingEngine engine, FakeSource source, _) = Make();
        var app = new FakeSession("a", "music", rawPeak: 0.8f);
        source.Sessions.Add(app);
        RunTicks(engine, 100); // engine has attenuated it

        Assert.True(engine.SetSessionVolume("a", 0.72f));
        RunTicks(engine, 50);

        Assert.Equal(0.72f, app.Volume, 3); // engine leaves the user's slider value alone
        engine.RestoreOriginalVolumes();
        Assert.Equal(0.72f, app.Volume, 3); // and restore keeps it
    }

    [Fact]
    public void Mixer_set_works_while_engine_is_not_ticking()
    {
        (LevelingEngine engine, FakeSource source, _) = Make();
        var app = new FakeSession("a", "music", rawPeak: 0.5f) { Volume = 1.0f };
        source.Sessions.Add(app);

        Assert.True(engine.SetSessionVolume("a", 0.25f)); // no Tick() ever ran
        Assert.Equal(0.25f, app.Volume, 3);
        Assert.False(engine.SetSessionVolume("missing", 0.5f));
    }

    [Fact]
    public void Legacy_exclusion_list_migrates_into_rules()
    {
        var appSettings = new AppSettings
        {
            ExcludedProcesses = ["oldgame", "daw"],
            Rules =
            [
                new AppSettings.RuleModel { ProcessName = "daw", Kind = "Fixed", FixedVolume = 0.8f },
            ],
        };

        var settings = new LevelerSettings();
        appSettings.ApplyTo(settings);

        // Explicit rule wins over the legacy list; plain legacy entries become Exclude.
        Assert.Equal(RuleKind.Fixed, settings.ResolveRule("daw").Kind);
        Assert.Equal(RuleKind.Exclude, settings.ResolveRule("OLDGAME").Kind);
        Assert.Equal(RuleKind.Level, settings.ResolveRule("anythingelse").Kind);
    }

    [Fact]
    public void CaptureRules_round_trips_through_the_persisted_model()
    {
        var settings = new LevelerSettings();
        settings.Rules["a"] = new AppRule("a", RuleKind.Fixed, 0.35f);
        settings.Rules["b"] = new AppRule("b", RuleKind.Exclude);

        var appSettings = new AppSettings();
        appSettings.CaptureRules(settings);

        var reloaded = new LevelerSettings();
        appSettings.ApplyTo(reloaded);

        Assert.Equal(RuleKind.Fixed, reloaded.ResolveRule("a").Kind);
        Assert.Equal(0.35f, reloaded.ResolveRule("a").SafeFixedVolume, 3);
        Assert.Equal(RuleKind.Exclude, reloaded.ResolveRule("b").Kind);
        Assert.Empty(appSettings.ExcludedProcesses); // legacy list fully retired
    }

    [Fact]
    public void Out_of_range_fixed_volume_from_settings_file_is_clamped()
    {
        var appSettings = new AppSettings
        {
            Rules = [new AppSettings.RuleModel { ProcessName = "x", Kind = "Fixed", FixedVolume = 4.2f }],
        };

        var settings = new LevelerSettings();
        appSettings.ApplyTo(settings);
        Assert.Equal(1.0f, settings.ResolveRule("x").SafeFixedVolume, 3);
    }
}
