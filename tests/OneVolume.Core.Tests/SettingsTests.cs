using OneVolume.Core.Leveling;
using OneVolume.Core.Settings;

namespace OneVolume.Core.Tests;

public class ProcessNamesTests
{
    [Fact]
    public void Parses_mixed_separators_extensions_and_whitespace()
    {
        IReadOnlyList<string> names = ProcessNames.Parse(" chrome.exe, , CS2 ;vlc.EXE,  ableton live ");
        Assert.Equal(["chrome", "CS2", "vlc", "ableton live"], names);
    }

    [Fact]
    public void Deduplicates_case_insensitively()
    {
        IReadOnlyList<string> names = ProcessNames.Parse("VLC, vlc.exe, Vlc");
        Assert.Equal(["VLC"], names);
    }

    [Fact]
    public void Null_and_empty_input_give_empty_list()
    {
        Assert.Empty(ProcessNames.Parse(null));
        Assert.Empty(ProcessNames.Parse(""));
        Assert.Empty(ProcessNames.Parse(" ,, ; "));
    }

    [Fact]
    public void Name_that_is_only_exe_suffix_is_dropped()
    {
        Assert.Empty(ProcessNames.Parse(".exe"));
    }
}

public class AppSettingsTests
{
    [Fact]
    public void ApplyTo_clamps_target_and_migrates_exclusions_into_rules()
    {
        var app = new AppSettings
        {
            TargetLevel = 5.0f, // corrupt/out-of-range value from a hand-edited file
            NightMode = true,
            ExcludedProcesses = ["game", "daw"],
        };

        var leveler = new LevelerSettings();
        app.ApplyTo(leveler);

        Assert.Equal(0.6f, leveler.TargetLevel, 3); // clamped to slider range
        Assert.True(leveler.NightMode);
        Assert.Equal(RuleKind.Exclude, leveler.ResolveRule("GAME").Kind); // case-insensitive
        Assert.Equal(RuleKind.Exclude, leveler.ResolveRule("daw").Kind);
    }

    [Fact]
    public void ApplyTo_clamps_negative_target_up_to_minimum()
    {
        var app = new AppSettings { TargetLevel = -3f };
        var leveler = new LevelerSettings();
        app.ApplyTo(leveler);
        Assert.Equal(0.05f, leveler.TargetLevel, 3);
    }
}
