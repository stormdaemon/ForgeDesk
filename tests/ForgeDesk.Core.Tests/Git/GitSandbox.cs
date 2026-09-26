using System.Diagnostics;
using System.Globalization;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Git;

/// <summary>
/// A <see cref="GitService"/> whose git processes use a private global config (and no system config),
/// plus helpers to build repositories, bare remotes and clones. Tests then behave the same on a
/// developer machine with signing or autocrlf set globally, on Linux and on Windows CI.
/// </summary>
internal sealed class GitSandbox : IDisposable
{
    public const string UserName = "Sandbox User";
    public const string UserEmail = "sandbox@example.com";

    private readonly List<IDisposable> _owned = [];
    private readonly TempDirectory _home = new("git-home");

    public GitSandbox(AppSettings? settings = null, IEnumerable<IGitCredentialProvider>? credentialProviders = null, bool withIdentity = true)
    {
        _owned.Add(_home);
        var identity = withIdentity ? $"[user]\n\tname = {UserName}\n\temail = {UserEmail}\n" : "[user]\n\tuseConfigOnly = true\n";
        GlobalConfigPath = _home.WriteFile("gitconfig",
            identity + "[commit]\n\tgpgsign = false\n[tag]\n\tgpgsign = false\n[init]\n\tdefaultBranch = main\n[core]\n\tautocrlf = false\n[advice]\n\tdetachedHead = false\n");
        Environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = GlobalConfigPath,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
        Settings = Substitute.For<ISettingsService>();
        Settings.Current.Returns(settings ?? AppSettings.Default);
        Service = new GitService(Settings, ProcessRunner.Instance, credentialProviders ?? [], Environment);
    }

    public string GlobalConfigPath { get; }

    public IReadOnlyDictionary<string, string?> Environment { get; }

    public ISettingsService Settings { get; }

    public GitService Service { get; }

    public IGitService Git => Service;

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public TestRepository CreateRepository(bool withInitialCommit = true, string branch = "main") =>
        Own(TestRepository.Create(withInitialCommit, branch));

    /// <summary>A bare repository to push to, seeded with <paramref name="seed"/>'s main branch.</summary>
    public TestRepository CreateRemoteFor(TestRepository seed)
    {
        var bare = Own(TestRepository.CreateBare());
        seed.Git("remote", "add", "origin", bare.Path);
        seed.Git("push", "-q", "-u", "origin", "main");
        return bare;
    }

    /// <summary>A second working copy of <paramref name="remotePath"/>, configured like the others.</summary>
    public string Clone(string remotePath)
    {
        var directory = NewDirectory("clone");
        var target = Path.Combine(directory, "work");
        Run(directory, "clone", "-q", remotePath, target);
        Run(target, "config", "commit.gpgsign", "false");
        return target;
    }

    public string NewDirectory(string prefix = "dir") => Own(new TempDirectory(prefix)).Path;

    public static string WriteFile(string repository, string relativePath, string content)
    {
        var full = Path.Combine([repository, .. relativePath.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public static string ReadFile(string repository, string relativePath) =>
        File.ReadAllText(Path.Combine([repository, .. relativePath.Split('/')]));

    /// <summary>Writes the files, stages everything and commits, optionally at a fixed date.</summary>
    public void Commit(string repository, string message, DateTimeOffset? date = null, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            WriteFile(repository, path, content);
        }

        Run(repository, "add", "-A");
        var environment = new Dictionary<string, string>();
        if (date is { } when)
        {
            var raw = when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " +0000";
            environment["GIT_AUTHOR_DATE"] = raw;
            environment["GIT_COMMITTER_DATE"] = raw;
        }

        RunWith(repository, environment, "commit", "-q", "-m", message);
    }

    public string Run(string workingDirectory, params string[] arguments) =>
        RunWith(workingDirectory, new Dictionary<string, string>(), arguments);

    public string RunWith(string workingDirectory, IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var (exitCode, stdout, stderr) = TryRunWith(workingDirectory, environment, arguments);
        return exitCode == 0
            ? stdout.Trim()
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {stderr}");
    }

    public (int ExitCode, string StandardOutput, string StandardError) TryRun(string workingDirectory, params string[] arguments) =>
        TryRunWith(workingDirectory, new Dictionary<string, string>(), arguments);

    private (int ExitCode, string StandardOutput, string StandardError) TryRunWith(string workingDirectory, IReadOnlyDictionary<string, string> environment, string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in Environment)
        {
            psi.Environment[key] = value;
        }

        foreach (var (key, value) in environment)
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)!;
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderrTask.GetAwaiter().GetResult());
    }

    private T Own<T>(T disposable)
        where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        foreach (var disposable in _owned)
        {
            disposable.Dispose();
        }
    }
}
