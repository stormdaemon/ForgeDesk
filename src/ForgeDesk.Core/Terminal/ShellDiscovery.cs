using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Terminal;

/// <summary>
/// Finds the shells installed on this machine, best first: PowerShell 7, Windows PowerShell,
/// Command Prompt, Git Bash and one profile per WSL distribution on Windows; the login shell,
/// bash, zsh and sh elsewhere. Profile ids are stable so the default shell survives restarts.
/// </summary>
internal sealed class ShellDiscovery : IShellDiscovery
{
    private static readonly IReadOnlyDictionary<string, string?> WslEnvironment = new Dictionary<string, string?>
    {
        // Recent wsl.exe builds then print UTF-8 instead of UTF-16.
        ["WSL_UTF8"] = "1",
    };

    private static readonly IReadOnlyDictionary<string, string> UnixDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bash"] = "Bash",
        ["zsh"] = "Zsh",
        ["fish"] = "Fish",
        ["sh"] = "sh",
        ["dash"] = "Dash",
        ["ksh"] = "Korn shell",
        ["tcsh"] = "tcsh",
        ["nu"] = "Nushell",
    };

    private readonly IShellEnvironment _environment;
    private readonly ILogger<ShellDiscovery> _logger;

    public ShellDiscovery(IShellEnvironment environment, ILogger<ShellDiscovery>? logger = null)
    {
        _environment = environment;
        _logger = logger ?? NullLogger<ShellDiscovery>.Instance;
    }

    public async Task<IReadOnlyList<ShellProfile>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        _environment.IsWindows
            ? await DiscoverWindowsAsync(cancellationToken).ConfigureAwait(false)
            : DiscoverUnix();

    private async Task<IReadOnlyList<ShellProfile>> DiscoverWindowsAsync(CancellationToken cancellationToken)
    {
        var profiles = new List<ShellProfile>();
        var systemRoot = Variable("SystemRoot") ?? Variable("windir") ?? @"C:\Windows";
        var programFiles = new[] { Variable("ProgramFiles"), Variable("ProgramW6432"), Variable("ProgramFiles(x86)") }
            .Where(p => p is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pwsh = _environment.FindInPath("pwsh")
            ?? FirstExisting(programFiles.Select(p => WindowsPath.Combine(p, @"PowerShell\7\pwsh.exe")));
        if (pwsh is not null)
        {
            profiles.Add(new ShellProfile("pwsh", "PowerShell", ShellKind.PowerShellCore, pwsh, "-NoLogo"));
        }

        var windowsPowerShell = FirstExisting([WindowsPath.Combine(systemRoot, @"System32\WindowsPowerShell\v1.0\powershell.exe")]);
        if (windowsPowerShell is not null)
        {
            profiles.Add(new ShellProfile("powershell", "Windows PowerShell", ShellKind.WindowsPowerShell, windowsPowerShell, "-NoLogo"));
        }

        var cmd = FirstExisting([Variable("ComSpec"), WindowsPath.Combine(systemRoot, @"System32\cmd.exe")]);
        if (cmd is not null)
        {
            profiles.Add(new ShellProfile("cmd", "Command Prompt", ShellKind.CommandPrompt, cmd, string.Empty));
        }

        var gitBash = FirstExisting(GitBashCandidates(programFiles));
        if (gitBash is not null)
        {
            profiles.Add(new ShellProfile("git-bash", "Git Bash", ShellKind.GitBash, gitBash, "--login -i"));
        }

        var wsl = FirstExisting([WindowsPath.Combine(systemRoot, @"System32\wsl.exe")]);
        if (wsl is not null)
        {
            foreach (var distribution in await ListWslDistributionsAsync(wsl, cancellationToken).ConfigureAwait(false))
            {
                profiles.Add(new ShellProfile($"wsl:{distribution}", $"{distribution} (WSL)", ShellKind.Wsl, wsl, $"-d {distribution}"));
            }
        }

        return profiles;
    }

    private IEnumerable<string?> GitBashCandidates(IReadOnlyList<string?> programFiles)
    {
        foreach (var root in programFiles)
        {
            yield return WindowsPath.Combine(root, @"Git\bin\bash.exe");
        }

        yield return WindowsPath.Combine(Variable("LOCALAPPDATA"), @"Programs\Git\bin\bash.exe");

        // Portable or custom installs: git.exe lives in <Git>\cmd, bash in <Git>\bin.
        if (_environment.FindInPath("git") is { } git)
        {
            var gitRoot = WindowsPath.GetDirectoryName(WindowsPath.GetDirectoryName(git));
            yield return WindowsPath.Combine(gitRoot, @"bin\bash.exe");
        }
    }

    private async Task<IReadOnlyList<string>> ListWslDistributionsAsync(string wsl, CancellationToken cancellationToken)
    {
        var output = await _environment.CaptureOutputAsync(wsl, ["-l", "-q"], WslEnvironment, cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            _logger.LogDebug("WSL is present but listed no distributions");
            return [];
        }

        return WslDistributions.Parse(output);
    }

    private List<ShellProfile> DiscoverUnix()
    {
        var profiles = new List<ShellProfile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? path)
        {
            if (path is null || !_environment.FileExists(path) || !seenPaths.Add(path))
            {
                return;
            }

            var id = Path.GetFileName(path);
            if (profiles.Exists(p => p.Id == id))
            {
                return;
            }

            var name = UnixDisplayNames.TryGetValue(id, out var display) ? display : id;
            profiles.Add(new ShellProfile(id, name, ShellKind.Unix, path, string.Empty));
        }

        Add(Variable("SHELL"));
        Add(_environment.FindInPath("bash"));
        Add(_environment.FindInPath("zsh"));
        Add(_environment.FindInPath("sh"));
        return profiles;
    }

    private string? Variable(string name) => _environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private string? FirstExisting(IEnumerable<string?> candidates) =>
        candidates.FirstOrDefault(c => c is not null && _environment.FileExists(c));

    /// <summary>Windows path helpers that behave the same on every OS (tests simulate Windows layouts on Linux).</summary>
    private static class WindowsPath
    {
        public static string? Combine(string? directory, string relative) =>
            string.IsNullOrEmpty(directory) ? null : directory.TrimEnd('\\', '/') + "\\" + relative;

        public static string? GetDirectoryName(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var separator = path.TrimEnd('\\', '/').LastIndexOfAny(['\\', '/']);
            return separator > 0 ? path[..separator] : null;
        }
    }
}
