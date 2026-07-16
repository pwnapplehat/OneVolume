namespace OneVolume.Core.Settings;

/// <summary>Parsing for the user-typed "never touch these apps" list.</summary>
public static class ProcessNames
{
    /// <summary>
    /// Splits a comma/semicolon-separated list into clean process names: trimmed,
    /// de-duplicated case-insensitively, with any ".exe" suffix removed (session process
    /// names never carry the extension).
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in (text ?? "").Split(',', ';'))
        {
            string name = raw.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4].TrimEnd();
            }

            if (name.Length > 0 && seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }
}
