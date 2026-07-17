using System.IO;

namespace OneVolume.App.Services;

/// <summary>An app the user can create a rule for, without it having to be running.</summary>
public sealed record InstalledApp(string DisplayName, string ProcessName);

/// <summary>
/// Enumerates installed applications from Start Menu shortcuts (per-user + all-users).
/// Shortcut targets give us the exe → process name, which is what audio sessions are
/// keyed by. This deliberately skips uninstallers/updaters and non-exe targets.
/// </summary>
public static class InstalledApps
{
    private static readonly string[] NoiseWords =
        ["uninstall", "uninst", "setup", "installer", "update", "updater", "repair", "readme", "help", "documentation", "website"];

    public static List<InstalledApp> Enumerate()
    {
        var byProcess = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                });
            }
            catch
            {
                continue;
            }

            foreach (string lnk in links)
            {
                string display = Path.GetFileNameWithoutExtension(lnk);
                string displayLower = display.ToLowerInvariant();
                if (NoiseWords.Any(displayLower.Contains))
                {
                    continue;
                }

                string? target = ResolveShortcutTarget(lnk);
                if (target is null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string exe = Path.GetFileNameWithoutExtension(target);
                string exeLower = exe.ToLowerInvariant();
                if (NoiseWords.Any(exeLower.Contains))
                {
                    continue;
                }

                // First shortcut wins per process; prefer the shortest friendly name
                // if we see the same exe again (e.g. "Word" over "Word (Office 365)").
                if (!byProcess.TryGetValue(exe, out InstalledApp? existing) ||
                    display.Length < existing.DisplayName.Length)
                {
                    byProcess[exe] = new InstalledApp(display, exe);
                }
            }
        }

        return [.. byProcess.Values.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Resolves a .lnk target via the Windows Script Host COM object.</summary>
    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string target = (string)shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch
        {
            return null; // broken shortcut — skip
        }
    }
}
