namespace ForgeDesk.Core.Processes;

/// <summary>Finds executables the way a shell would (PATH + PATHEXT on Windows).</summary>
public static class ExecutableLocator
{
    public static string? Find(string name, IEnumerable<string>? extraDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Path.IsPathRooted(name))
        {
            return File.Exists(name) ? name : null;
        }

        var directories = new List<string>();
        if (extraDirectories is not null)
        {
            directories.AddRange(extraDirectories);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        var hasExtension = Path.HasExtension(name);
        foreach (var directory in directories)
        {
            string candidateBase;
            try
            {
                candidateBase = Path.Combine(directory.Trim('"'), name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (hasExtension && File.Exists(candidateBase))
            {
                return candidateBase;
            }

            foreach (var ext in extensions)
            {
                var candidate = candidateBase + ext.ToLowerInvariant();
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
