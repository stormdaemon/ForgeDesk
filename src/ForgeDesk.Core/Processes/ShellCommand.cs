namespace ForgeDesk.Core.Processes;

/// <summary>
/// Builds a <see cref="ProcessSpec"/> that runs a free-form command line through the
/// platform shell (cmd.exe on Windows, /bin/sh elsewhere), which is what users expect
/// for "npm run build", "cargo test -- --nocapture" or "make &amp;&amp; make install".
/// </summary>
public static class ShellCommand
{
    public static ProcessSpec Create(string commandLine, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        if (OperatingSystem.IsWindows())
        {
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(comspec) || !File.Exists(comspec))
            {
                comspec = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            }

            // /d: skip AutoRun, /s + outer quotes: keep the command line verbatim, /c: run then exit.
            return new ProcessSpec
            {
                FileName = comspec,
                RawArguments = $"/d /s /c \"{commandLine}\"",
                WorkingDirectory = workingDirectory,
                Environment = environment ?? new Dictionary<string, string?>(),
                Timeout = timeout,
            };
        }

        return new ProcessSpec
        {
            FileName = "/bin/sh",
            Arguments = ["-c", commandLine],
            WorkingDirectory = workingDirectory,
            Environment = environment ?? new Dictionary<string, string?>(),
            Timeout = timeout,
        };
    }
}
