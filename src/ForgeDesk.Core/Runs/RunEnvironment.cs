namespace ForgeDesk.Core.Runs;

/// <summary>Environment applied to every run, before the request's own overrides.</summary>
internal static class RunEnvironment
{
    private static readonly KeyValuePair<string, string?>[] Defaults =
    [
        // Output goes to a pipe: without these, Python buffers it (nothing shows until exit) and,
        // on Windows, encodes it in the ANSI code page instead of UTF-8.
        new("PYTHONUNBUFFERED", "1"),
        new("PYTHONIOENCODING", "utf-8"),

        // The log view shows plain text; fewer escape codes also means cleaner logs on disk.
        new("FORCE_COLOR", "0"),
        new("NO_COLOR", "1"),

        new("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
    ];

    public static IReadOnlyDictionary<string, string?> Build(IReadOnlyDictionary<string, string?>? overrides)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string?>(Defaults, comparer);
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    environment[key] = value;
                }
            }
        }

        return environment;
    }
}
