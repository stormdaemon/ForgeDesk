using System.Text.RegularExpressions;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Finds the git executable: the path configured in settings (authoritative when set), then PATH,
/// then the usual Git for Windows install folders. Successful lookups are cached until the
/// configured path changes; failures are not, so installing Git while ForgeDesk runs just works.
/// </summary>
internal sealed partial class GitLocator
{
    /// <summary>Oldest git with every command and option ForgeDesk relies on (--diff-merges, --force-if-includes…).</summary>
    public static readonly Version MinimumVersion = new(2, 31);

    public const string DownloadUrl = "https://git-scm.com/download/win";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private readonly ISettingsService _settings;
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedInstallation? _cache;

    public GitLocator(ISettingsService settings, IProcessRunner runner, IReadOnlyDictionary<string, string?> environment, Func<string, string?>? getEnvironmentVariable = null)
    {
        _settings = settings;
        _runner = runner;
        _environment = environment;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _settings.Changed += OnSettingsChanged;
    }

    public async Task<GitInstallation?> FindAsync(CancellationToken cancellationToken)
    {
        var configured = ConfiguredPath(_settings.Current);
        if (Volatile.Read(ref _cache) is { } hit && hit.ConfiguredPath == configured)
        {
            return hit.Installation;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is { } cached && cached.ConfiguredPath == configured)
            {
                return cached.Installation;
            }

            var installation = await LocateAsync(configured, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cache, installation is null ? null : new CachedInstallation(configured, installation));
            return installation;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Like <see cref="FindAsync"/> but throws GitNotFound (with guidance) when git is missing or too old.</summary>
    public async Task<GitInstallation> RequireAsync(CancellationToken cancellationToken)
    {
        var installation = await FindAsync(cancellationToken).ConfigureAwait(false);
        if (installation is null)
        {
            var configured = ConfiguredPath(_settings.Current);
            throw configured is not null
                ? new ForgeException(ErrorKind.GitNotFound, "The Git program set in Settings can't be used.",
                    "Correct the Git path in Settings › Git, or clear it to use the Git installed on this computer.",
                    $"Configured path: {configured}")
                : new ForgeException(ErrorKind.GitNotFound, "Git is not installed, or ForgeDesk can't find it.",
                    $"Install Git from {DownloadUrl} and try again.");
        }

        if (Version.TryParse(installation.Version, out var version) && version < MinimumVersion)
        {
            throw new ForgeException(ErrorKind.GitNotFound, $"Git {installation.Version} is too old for ForgeDesk.",
                $"Install Git {MinimumVersion} or later from {DownloadUrl}.", installation.ExecutablePath);
        }

        return installation;
    }

    public void Invalidate() => Volatile.Write(ref _cache, null);

    /// <summary>"git version 2.45.1.windows.1" → "2.45.1"; null when the text isn't git's version banner.</summary>
    public static string? ParseVersion(string output)
    {
        var match = VersionBanner().Match(output ?? string.Empty);
        return match.Success ? match.Groups["version"].Value : null;
    }

    /// <summary>Default Git for Windows install locations, in order of preference.</summary>
    public static IReadOnlyList<string> WellKnownWindowsLocations(Func<string, string?> getEnvironmentVariable)
    {
        var locations = new List<string>();
        foreach (var variable in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
        {
            if (getEnvironmentVariable(variable) is { Length: > 0 } root)
            {
                locations.Add(Path.Combine(root, "Git", "cmd", "git.exe"));
            }
        }

        if (getEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
        {
            locations.Add(Path.Combine(local, "Programs", "Git", "cmd", "git.exe"));
        }

        return locations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Candidates for a configured path: the file itself, or — when the user picked the Git install
    /// folder rather than the executable — the executables inside it.
    /// </summary>
    public static IEnumerable<string> ExpandConfiguredPath(string configured)
    {
        if (!Directory.Exists(configured))
        {
            yield return configured;
            yield break;
        }

        var executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
        yield return Path.Combine(configured, "cmd", executable);
        yield return Path.Combine(configured, "bin", executable);
        yield return Path.Combine(configured, executable);
    }

    private IEnumerable<string> DiscoveryCandidates()
    {
        if (ExecutableLocator.Find(OperatingSystem.IsWindows() ? "git.exe" : "git") is { } onPath)
        {
            yield return onPath;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var location in WellKnownWindowsLocations(_getEnvironmentVariable))
            {
                yield return location;
            }
        }
    }

    private async Task<GitInstallation?> LocateAsync(string? configured, CancellationToken cancellationToken)
    {
        var candidates = configured is not null ? ExpandConfiguredPath(configured) : DiscoveryCandidates();
        foreach (var candidate in candidates)
        {
            var installation = await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (installation is not null)
            {
                return installation;
            }
        }

        return null;
    }

    private async Task<GitInstallation?> ProbeAsync(string executable, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            return null;
        }

        var environment = new Dictionary<string, string?>(_environment, StringComparer.Ordinal) { ["LC_ALL"] = "C", ["LANG"] = "C" };
        var spec = new ProcessSpec
        {
            FileName = executable,
            Arguments = ["--version"],
            Environment = environment,
            Timeout = ProbeTimeout,
            MaxCapturedChars = 4096,
        };

        try
        {
            var result = await _runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            var version = result.Succeeded ? ParseVersion(result.StandardOutput) : null;
            return version is null ? null : new GitInstallation(Path.GetFullPath(executable), version);
        }
        catch (ForgeException)
        {
            // Not startable (corrupt install, wrong architecture…): try the next candidate.
            return null;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (Volatile.Read(ref _cache) is { } cached && cached.ConfiguredPath != ConfiguredPath(settings))
        {
            Invalidate();
        }
    }

    private static string? ConfiguredPath(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.GitExecutablePath) ? null : settings.GitExecutablePath.Trim().Trim('"');

    [GeneratedRegex(@"git version (?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionBanner();

    private sealed record CachedInstallation(string? ConfiguredPath, GitInstallation Installation);
}
