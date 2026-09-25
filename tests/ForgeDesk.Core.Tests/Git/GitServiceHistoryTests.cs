using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitServiceHistoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    [Fact]
    public async Task Commit_with_a_multi_line_unicode_message()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("a.txt", "a\n");
        repo.Git("add", "a.txt");
        const string message = "Ajoute l'écran « Paramètres » ✨\r\n\r\nDétails : \"quotes\", $vars, `backticks`\n# 123 is kept\n";

        var commit = await Git.CommitAsync(repo.Path, new GitCommitOptions(message), Ct);

        commit.Subject.Should().Be("Ajoute l'écran « Paramètres » ✨");
        commit.Body.Should().Be("Détails : \"quotes\", $vars, `backticks`\n# 123 is kept");
        commit.Sha.Should().Be(repo.Git("rev-parse", "HEAD"));
        commit.Author.Name.Should().Be("Test User");
        commit.Parents.Should().ContainSingle();
    }

    [Fact]
    public async Task Stage_all_commit_includes_untracked_files()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "changed\n");
        repo.WriteFile("brand-new.txt", "new\n");

        await Git.CommitAsync(repo.Path, new GitCommitOptions("Everything", StageAll: true), Ct);

        (await Git.GetStatusAsync(repo.Path, Ct)).IsClean.Should().BeTrue();
    }

    [Fact]
    public async Task Amend_replaces_the_last_commit()
    {
        var repo = _sandbox.CreateRepository();
        var first = repo.Git("rev-parse", "HEAD");
        repo.WriteFile("fix.txt", "fix\n");
        repo.Git("add", "fix.txt");

        var amended = await Git.CommitAsync(repo.Path, new GitCommitOptions("Fixed message", Amend: true), Ct);

        amended.Subject.Should().Be("Fixed message");
        repo.Git("rev-list", "--count", "HEAD").Should().Be("1");
        amended.Sha.Should().NotBe(first);
        repo.Git("show", "--name-only", "--format=", "HEAD").Should().Contain("fix.txt");
    }

    [Fact]
    public async Task First_commit_in_an_unborn_repository()
    {
        var repo = _sandbox.CreateRepository(withInitialCommit: false);
        repo.WriteFile("hello.txt", "hi\n");

        var commit = await Git.CommitAsync(repo.Path, new GitCommitOptions("Initial", StageAll: true), Ct);

        commit.Parents.Should().BeEmpty();
        (await Git.GetStatusAsync(repo.Path, Ct)).IsUnborn.Should().BeFalse();
    }

    [Fact]
    public async Task Empty_message_is_invalid()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.CommitAsync(repo.Path, new GitCommitOptions("  \n  "), Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Nothing_staged_means_nothing_to_commit()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("untracked.txt", "u\n");

        var act = () => Git.CommitAsync(repo.Path, new GitCommitOptions("Nothing"), Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NothingToCommit);
    }

    [Fact]
    public async Task Log_pages_through_history_newest_first()
    {
        var repo = _sandbox.CreateRepository();
        for (var i = 1; i <= 5; i++)
        {
            _sandbox.Commit(repo.Path, $"Commit {i}", T0.AddMinutes(i), ($"f{i}.txt", $"{i}\n"));
        }

        var page1 = await Git.GetLogAsync(repo.Path, new GitLogQuery { Take = 2 }, Ct);
        var page2 = await Git.GetLogAsync(repo.Path, new GitLogQuery { Skip = 2, Take = 2 }, Ct);
        var all = await Git.GetLogAsync(repo.Path, new GitLogQuery(), Ct);

        page1.Select(c => c.Subject).Should().Equal("Commit 5", "Commit 4");
        page2.Select(c => c.Subject).Should().Equal("Commit 3", "Commit 2");
        all.Should().HaveCount(6);
        all[0].Refs.Should().Contain("HEAD -> main");
        all[0].Author.When.Should().Be(T0.AddMinutes(5));
        all[^1].Parents.Should().BeEmpty();
    }

    [Fact]
    public async Task Log_of_other_branches_and_paths()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("checkout", "-q", "-b", "feature");
        _sandbox.Commit(repo.Path, "Feature work", T0.AddHours(1), ("feature.txt", "f\n"));
        repo.Git("checkout", "-q", "main");
        _sandbox.Commit(repo.Path, "Main work", T0.AddHours(2), ("src/main.txt", "m\n"));
        repo.Git("tag", "v1.0");

        var mainLog = await Git.GetLogAsync(repo.Path, new GitLogQuery(), Ct);
        var featureLog = await Git.GetLogAsync(repo.Path, new GitLogQuery { Revision = "feature" }, Ct);
        var allLog = await Git.GetLogAsync(repo.Path, new GitLogQuery { AllBranches = true }, Ct);
        var pathLog = await Git.GetLogAsync(repo.Path, new GitLogQuery { PathFilter = "src/main.txt" }, Ct);

        mainLog.Select(c => c.Subject).Should().NotContain("Feature work");
        featureLog.Select(c => c.Subject).Should().Contain("Feature work").And.NotContain("Main work");
        allLog.Select(c => c.Subject).Should().Contain(["Feature work", "Main work"]);
        allLog[0].Refs.Should().Contain("tag: v1.0");
        pathLog.Should().ContainSingle().Which.Subject.Should().Be("Main work");
    }

    [Fact]
    public async Task Log_search_matches_message_or_author_case_insensitively()
    {
        var repo = _sandbox.CreateRepository();
        _sandbox.Commit(repo.Path, "Fix login crash", T0.AddMinutes(1), ("a.txt", "1\n"));
        _sandbox.RunWith(repo.Path, new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Loginov",
            ["GIT_AUTHOR_DATE"] = $"{T0.AddMinutes(2).ToUnixTimeSeconds()} +0000",
            ["GIT_COMMITTER_DATE"] = $"{T0.AddMinutes(2).ToUnixTimeSeconds()} +0000",
        }, "commit", "-q", "--allow-empty", "-m", "Unrelated change");
        _sandbox.Commit(repo.Path, "Update docs", T0.AddMinutes(3), ("b.txt", "2\n"));
        _sandbox.Commit(repo.Path, "LOGIN: remember me (regex chars .*)", T0.AddMinutes(4), ("c.txt", "3\n"));

        var results = await Git.GetLogAsync(repo.Path, new GitLogQuery { Search = "login" }, Ct);
        var regexLike = await Git.GetLogAsync(repo.Path, new GitLogQuery { Search = "(regex chars .*)" }, Ct);
        var paged = await Git.GetLogAsync(repo.Path, new GitLogQuery { Search = "login", Skip = 1, Take = 1 }, Ct);

        results.Select(c => c.Subject).Should().Equal("LOGIN: remember me (regex chars .*)", "Unrelated change", "Fix login crash");
        regexLike.Should().ContainSingle();
        paged.Should().ContainSingle().Which.Subject.Should().Be("Unrelated change");
    }

    [Fact]
    public async Task Log_of_an_unborn_repository_is_empty()
    {
        var repo = _sandbox.CreateRepository(withInitialCommit: false);

        (await Git.GetLogAsync(repo.Path, new GitLogQuery(), Ct)).Should().BeEmpty();
        (await Git.GetLogAsync(repo.Path, new GitLogQuery { Search = "x" }, Ct)).Should().BeEmpty();
        (await Git.CountCommitsAsync(repo.Path, cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Log_of_an_unknown_revision_is_not_found()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.GetLogAsync(repo.Path, new GitLogQuery { Revision = "no-such-branch" }, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.NotFound);
        error.Message.Should().Contain("no-such-branch");
    }

    [Fact]
    public async Task Log_refuses_revisions_that_look_like_options()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.GetLogAsync(repo.Path, new GitLogQuery { Revision = "--output=/tmp/pwned" }, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Commit_details_list_files_with_stats_renames_and_binaries()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Base", ("keep.txt", "1\n2\n3\n"), ("move-me.txt", "a\nb\nc\nd\ne\n"), ("remove.txt", "x\n"));
        repo.WriteFile("keep.txt", "1\ntwo\n3\n4\n");
        repo.Git("mv", "move-me.txt", "moved.txt");
        File.Delete(repo.Combine("remove.txt"));
        File.WriteAllBytes(repo.Combine("blob.bin"), [0, 1, 2, 3]);
        repo.Commit("Mixed changes\n\nWith a body.");
        var sha = repo.Git("rev-parse", "HEAD");

        var details = await Git.GetCommitDetailsAsync(repo.Path, sha, Ct);

        details.Commit.Sha.Should().Be(sha);
        details.Commit.Body.Should().Be("With a body.");
        details.Files.Should().BeEquivalentTo(new[]
        {
            new GitFileChange("keep.txt", null, GitFileState.Modified, 2, 1, false),
            new GitFileChange("moved.txt", "move-me.txt", GitFileState.Renamed, 0, 0, false),
            new GitFileChange("remove.txt", null, GitFileState.Deleted, 0, 1, false),
            new GitFileChange("blob.bin", null, GitFileState.Added, 0, 0, true),
        });
    }

    [Fact]
    public async Task Commit_details_of_root_and_merge_commits()
    {
        var repo = _sandbox.CreateRepository();
        var root = repo.Git("rev-parse", "HEAD");
        repo.Git("checkout", "-q", "-b", "side");
        repo.Commit("Side", ("side.txt", "s\n"));
        repo.Git("checkout", "-q", "main");
        repo.Commit("Main", ("main.txt", "m\n"));
        repo.Git("merge", "-q", "--no-edit", "side");

        var rootDetails = await Git.GetCommitDetailsAsync(repo.Path, root, Ct);
        var mergeDetails = await Git.GetCommitDetailsAsync(repo.Path, "HEAD", Ct);

        rootDetails.Files.Should().ContainSingle().Which.Should().Be(new GitFileChange("README.md", null, GitFileState.Added, 1, 0, false));
        mergeDetails.Commit.IsMerge.Should().BeTrue();
        mergeDetails.Files.Should().ContainSingle().Which.Path.Should().Be("side.txt");
    }

    [Fact]
    public async Task Commit_details_of_an_unknown_commit()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.GetCommitDetailsAsync(repo.Path, "deadbeef", Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.NotFound);
        error.Message.Should().Contain("deadbeef");
    }

    [Fact]
    public async Task Count_commits_with_range_and_date()
    {
        // The initial commit is dated "now": the recent ones are placed after it.
        var soon = DateTimeOffset.UtcNow.AddDays(1);
        var repo = _sandbox.CreateRepository();
        _sandbox.Commit(repo.Path, "Old", T0.AddDays(-10), ("a.txt", "a\n"));
        repo.Git("branch", "base");
        _sandbox.Commit(repo.Path, "Recent 1", soon, ("b.txt", "b\n"));
        _sandbox.Commit(repo.Path, "Recent 2", soon.AddHours(1), ("c.txt", "c\n"));

        (await Git.CountCommitsAsync(repo.Path, cancellationToken: Ct)).Should().Be(4);
        (await Git.CountCommitsAsync(repo.Path, "base..HEAD", cancellationToken: Ct)).Should().Be(2);
        (await Git.CountCommitsAsync(repo.Path, since: soon.AddMinutes(-1), cancellationToken: Ct)).Should().Be(2);
    }

    [Fact]
    public void Search_results_are_merged_by_date_without_duplicates()
    {
        static GitCommit C(string sha, int minute) => new()
        {
            Sha = sha,
            Subject = sha,
            Author = new GitSignature("a", "a@x", T0.AddMinutes(minute)),
            Committer = new GitSignature("a", "a@x", T0.AddMinutes(minute)),
        };

        var merged = GitService.MergeByDate([C("e", 5), C("c", 3), C("a", 1)], [C("d", 4), C("c", 3), C("b", 2)]);

        merged.Select(c => c.Sha).Should().Equal("e", "d", "c", "b", "a");
    }

    public void Dispose() => _sandbox.Dispose();
}
