using System.Diagnostics;
using System.Globalization;
using Dapper;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Tests.Infrastructure;

/// <summary>An <see cref="IClock"/> whose time only moves when a test says so.</summary>
public sealed class AdjustableClock(DateTimeOffset start) : IClock
{
    private long _ticks = start.UtcTicks;

    public AdjustableClock()
        : this(new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>Seeds rows of the <c>projects</c> table so tests of dependent tables satisfy foreign keys.</summary>
public static class ProjectRowSeeding
{
    public static async Task<string> InsertProjectRowAsync(this TestDatabase db, string name = "Demo")
    {
        var id = Ids.New();
        await db.Database.UseAsync(c => c.ExecuteAsync(
            "INSERT INTO projects (id, name, path, added_at) VALUES (@Id, @Name, @Path, @AddedAt)",
            new { Id = id, Name = name, Path = Path.Combine(Path.GetTempPath(), "forgedesk-project-" + id), AddedAt = DateTimeOffset.UtcNow }));
        return id;
    }
}

/// <summary>
/// Builds realistic history in a <see cref="TestRepository"/>: commits dated in the past
/// (GIT_AUTHOR_DATE / GIT_COMMITTER_DATE) by chosen authors.
/// </summary>
public static class TestRepositoryHistory
{
    /// <summary>Writes <paramref name="files"/>, stages everything and commits at <paramref name="when"/> as the given author.</summary>
    public static void CommitDated(
        this TestRepository repo,
        DateTimeOffset when,
        string message,
        string authorName = "Test User",
        string authorEmail = "test@example.com",
        params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            repo.WriteFile(path, content);
        }

        var date = when.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = date,
            ["GIT_COMMITTER_DATE"] = date,
            ["GIT_AUTHOR_NAME"] = authorName,
            ["GIT_AUTHOR_EMAIL"] = authorEmail,
            ["GIT_COMMITTER_NAME"] = authorName,
            ["GIT_COMMITTER_EMAIL"] = authorEmail,
        };

        RunGit(repo.Path, environment, "add", "-A");
        RunGit(repo.Path, environment, "commit", "--allow-empty", "-m", message);
    }

    private static void RunGit(string workingDirectory, IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
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
        foreach (var (key, value) in environment)
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        stdout.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        }
    }
}
