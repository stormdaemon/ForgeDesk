using System.Diagnostics;

namespace ForgeDesk.Core.Tests.Infrastructure;

/// <summary>
/// A real git repository in a temp folder, driven through the git CLI, with an isolated
/// configuration so tests never depend on the developer's global git settings.
/// </summary>
public sealed class TestRepository : IDisposable
{
    private readonly TempDirectory _dir;

    private TestRepository(TempDirectory dir) => _dir = dir;

    public string Path => _dir.Path;

    public static TestRepository Create(bool withInitialCommit = true, string branch = "main")
    {
        var repo = new TestRepository(new TempDirectory("repo"));
        repo.Git("init", "--initial-branch", branch);
        repo.Git("config", "user.name", "Test User");
        repo.Git("config", "user.email", "test@example.com");
        repo.Git("config", "commit.gpgsign", "false");
        repo.Git("config", "core.autocrlf", "false");
        if (withInitialCommit)
        {
            repo.WriteFile("README.md", "# Test\n");
            repo.Git("add", "-A");
            repo.Git("commit", "-m", "Initial commit");
        }

        return repo;
    }

    /// <summary>Creates a bare repository usable as a remote.</summary>
    public static TestRepository CreateBare()
    {
        var repo = new TestRepository(new TempDirectory("bare"));
        repo.Git("init", "--bare", "--initial-branch", "main");
        return repo;
    }

    public string WriteFile(string relativePath, string content) => _dir.WriteFile(relativePath, content);

    public string Combine(params string[] parts) => _dir.Combine(parts);

    public void Commit(string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            WriteFile(path, content);
        }

        Git("add", "-A");
        Git("commit", "-m", message);
    }

    /// <summary>Runs git and returns trimmed stdout; throws on failure.</summary>
    public string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout.Trim();
    }

    public void Dispose() => _dir.Dispose();
}
