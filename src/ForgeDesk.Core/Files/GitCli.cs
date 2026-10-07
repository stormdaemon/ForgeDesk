using System.Text;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.Core.Files;

/// <summary>
/// The few read-only git commands the Files domain relies on (check-ignore, ls-files, grep).
/// Git is optional here: every caller has a managed fallback when git is missing or fails.
/// </summary>
internal sealed class GitCli(IProcessRunner runner, ISettingsService? settings = null)
{
    /// <summary>Streaming commands deliver output through callbacks; only stderr needs capturing.</summary>
    private const int StreamingCaptureLimit = 64 * 1024;

    private static readonly IReadOnlyDictionary<string, string?> GitEnvironment = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        // English messages, but UTF-8 character handling: under plain "C", git grep folds case and runs PCRE
        // on ASCII only, so "-i ÉCOLE" or "caf.$" would miss what the managed engine finds.
        ["LC_ALL"] = "C.UTF-8",

        // Read-only commands must never take index.lock and collide with the user's own git operations.
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_PAGER"] = "cat",
    };

    private string? _discovered;

    /// <summary>
    /// Absolute path of git: the configured one, else the first on PATH, else a default Git for Windows
    /// install; null when none exists. Never a bare "git": Windows would first look for git.exe in
    /// ForgeDesk's current directory, which may be a freshly cloned, untrusted repository.
    /// </summary>
    public string? Executable
    {
        get
        {
            if (settings?.Current.GitExecutablePath is { Length: > 0 } configured && File.Exists(configured))
            {
                return configured;
            }

            if (Volatile.Read(ref _discovered) is { } cached && File.Exists(cached))
            {
                return cached;
            }

            var found = ExecutableLocator.Find(OperatingSystem.IsWindows() ? "git.exe" : "git")
                ?? (OperatingSystem.IsWindows()
                    ? Git.GitLocator.WellKnownWindowsLocations(Environment.GetEnvironmentVariable).FirstOrDefault(File.Exists)
                    : null);
            if (found is not null)
            {
                found = Path.GetFullPath(found);
                Volatile.Write(ref _discovered, found);
            }

            return found;
        }
    }

    /// <summary>True when <paramref name="directory"/> or one of its parents holds a .git folder (or worktree file).</summary>
    public static bool IsInsideRepository(string directory)
    {
        try
        {
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                var marker = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(marker) || File.Exists(marker))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Unreadable parents: treat as not a repository and let the managed code handle it.
        }

        return false;
    }

    /// <summary>Runs git and captures its output; returns null when git is not installed (or cannot start).</summary>
    public async Task<ProcessResult?> TryRunAsync(string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken,
        string? standardInput = null, TimeSpan? timeout = null)
    {
        try
        {
            return await runner.RunAsync(CreateSpec(workingDirectory, arguments, standardInput, timeout, 32 * 1024 * 1024), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ForgeException ex) when (ex.Kind is ErrorKind.ToolNotFound or ErrorKind.PathNotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs git and hands every standard-output line to <paramref name="onLine"/> as it arrives
    /// (nothing is accumulated). Returns null when git is not installed.
    /// </summary>
    public async Task<ProcessResult?> TryStreamAsync(string workingDirectory, IEnumerable<string> arguments, Action<string> onLine,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        try
        {
            return await runner.RunAsync(
                CreateSpec(workingDirectory, arguments, null, timeout, StreamingCaptureLimit),
                line =>
                {
                    if (line.Stream == OutputStream.StandardOutput)
                    {
                        onLine(line.Text);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex) when (ex.Kind is ErrorKind.ToolNotFound or ErrorKind.PathNotFound)
        {
            return null;
        }
    }

    /// <summary>Splits NUL-separated output (-z) into entries.</summary>
    public static IEnumerable<string> SplitNul(string output) =>
        output.Split('\0').Select(s => s.Trim('\n', '\r')).Where(s => s.Length > 0);

    /// <summary>
    /// Undoes git's C-style quoting of unusual paths ("\"tab\\there.txt\"", octal-escaped bytes).
    /// With core.quotepath=false, UTF-8 names come through unquoted.
    /// </summary>
    public static string UnquotePath(string raw)
    {
        if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
        {
            return raw;
        }

        var bytes = new List<byte>(raw.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 1; i < raw.Length - 1; i++)
        {
            var c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length - 1)
            {
                AppendUtf8(bytes, raw, ref i, utf8);
                continue;
            }

            var next = raw[++i];
            switch (next)
            {
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 't': bytes.Add(9); break;
                case 'n': bytes.Add(10); break;
                case 'v': bytes.Add(11); break;
                case 'f': bytes.Add(12); break;
                case 'r': bytes.Add(13); break;
                case >= '0' and <= '7' when i + 2 < raw.Length - 1:
                    bytes.Add((byte)(((next - '0') << 6) | ((raw[i + 1] - '0') << 3) | (raw[i + 2] - '0')));
                    i += 2;
                    break;
                default:
                    bytes.Add((byte)next);
                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void AppendUtf8(List<byte> bytes, string text, ref int i, Span<byte> scratch)
    {
        var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
        var written = Encoding.UTF8.GetBytes(text.AsSpan(i, length), scratch);
        for (var k = 0; k < written; k++)
        {
            bytes.Add(scratch[k]);
        }

        i += length - 1;
    }

    private ProcessSpec CreateSpec(string workingDirectory, IEnumerable<string> arguments, string? standardInput, TimeSpan? timeout, int maxCapturedChars) => new()
    {
        FileName = Executable ?? throw new ForgeException(ErrorKind.ToolNotFound, "Git is not installed."),
        Arguments = ["-c", "core.quotepath=false", "-c", "color.ui=false", .. arguments],
        WorkingDirectory = workingDirectory,
        Environment = GitEnvironment,
        StandardInput = standardInput,
        Timeout = timeout,
        MaxCapturedChars = maxCapturedChars,
    };
}
