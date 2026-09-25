using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Releases;

public sealed class ReleaseServicePrepareTests : IDisposable
{
    private const string HeadSha = "1234567890abcdef1234567890abcdef12345678";
    private static readonly GitHubRepoRef Repo = new("octo", "forge");

    private readonly TempDirectory _dir = new("prepare");
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IGitHubService _github = Substitute.For<IGitHubService>();
    private readonly AdjustableClock _clock = new(DateTimeOffset.UtcNow);
    private readonly ReleaseService _service;
    private readonly Project _project;
    private GitStatus _status = new() { Branch = "main", HeadSha = HeadSha, Upstream = "origin/main" };
    private IReadOnlyList<GitCommit> _commits = [];

    public ReleaseServicePrepareTests()
    {
        _project = new Project { Id = "p1", Name = "forge", Path = _dir.Path, GitHub = Repo };
        _service = new ReleaseService(_git, _github, Substitute.For<IActivityLog>(), _clock);
        _git.IsRepositoryAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns(true);
        _git.GetStatusAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns(_ => _status);
        _git.GetDefaultBranchAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns("main");
        _git.GetTagsAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([
            Tag("v0.9.0"), Tag("v1.1.0"), Tag("nightly"), Tag("v1.0.0"), Tag("v1.1.0-rc.1"),
        ]);
        _git.GetLogAsync(_dir.Path, Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>()).Returns(_ => _commits);
        _github.IsSignedIn.Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Prepares_a_minor_release_after_new_features()
    {
        _commits = [Commit("feat(ui): add dark mode"), Commit("fix: crash on start"), Commit("Merge branch 'x'", parents: 2)];

        var context = await _service.PrepareAsync(_project, Ct);

        context.Blockers.Should().BeEmpty();
        context.Warnings.Should().BeEmpty();
        context.LatestTag.Should().Be("v1.1.0");
        context.LatestVersion.Should().Be("1.1.0");
        context.TagPrefix.Should().Be("v");
        context.CurrentBranch.Should().Be("main");
        context.DefaultBranch.Should().Be("main");
        context.HeadSha.Should().Be(HeadSha);
        context.CommitsSinceLatest.Should().HaveCount(3);
        context.Suggestions.Select(s => (s.Label, s.Version)).Should().Equal(("Minor", "1.2.0"), ("Patch", "1.1.1"), ("Major", "2.0.0"));
        context.Suggestions[0].Reason.Should().Be("Recommended: 1 new feature since v1.1.0.");
        context.DraftNotes.Should().Contain("### ✨ Features\n- **ui:** Add dark mode")
            .And.Contain("### 🐛 Fixes")
            .And.Contain("https://github.com/octo/forge/compare/v1.1.0...v1.2.0")
            .And.NotContain("Merge branch");
        await _git.Received(1).GetLogAsync(_dir.Path, Arg.Is<GitLogQuery>(q => q.Revision == "v1.1.0..HEAD" && q.Take == ReleaseService.MaxCommits), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Breaking_changes_recommend_a_major_release()
    {
        _commits = [Commit("feat!: drop the v1 API"), Commit("feat: add search")];

        var context = await _service.PrepareAsync(_project, Ct);

        context.Suggestions[0].Should().Be(new VersionSuggestion("Major", "2.0.0", "Recommended: 1 breaking change since v1.1.0."));
    }

    [Fact]
    public async Task Fixes_only_recommend_a_patch_release()
    {
        _commits = [Commit("fix: typo"), Commit("chore: bump deps"), Commit("Tidy up")];

        var context = await _service.PrepareAsync(_project, Ct);

        context.Suggestions.Select(s => s.Version).Should().Equal("1.1.1", "1.2.0", "2.0.0");
        context.Suggestions[0].Reason.Should().Contain("only fixes and maintenance");
    }

    [Fact]
    public async Task A_first_release_offers_0_1_0_and_1_0_0()
    {
        _git.GetTagsAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([Tag("nightly")]);
        _commits = [Commit("Initial commit")];

        var context = await _service.PrepareAsync(_project, Ct);

        context.LatestTag.Should().BeNull();
        context.Suggestions.Select(s => s.Version).Should().Equal("0.1.0", "1.0.0");
        context.TagPrefix.Should().Be("v");
        context.DraftNotes.Should().NotContain("Full changelog");
        await _git.Received(1).GetLogAsync(_dir.Path, Arg.Is<GitLogQuery>(q => q.Revision == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pending_prerelease_suggests_the_stable_release_or_the_next_preview()
    {
        _git.GetTagsAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([Tag("v1.1.0"), Tag("v2.0.0-rc.1")]);
        _commits = [Commit("fix: regression")];

        var context = await _service.PrepareAsync(_project, Ct);

        context.LatestTag.Should().Be("v2.0.0-rc.1");
        context.Suggestions.Select(s => (s.Label, s.Version)).Should().Equal(("Stable release", "2.0.0"), ("Next pre-release", "2.0.0-rc.2"));
    }

    [Fact]
    public async Task Tags_without_prefix_keep_that_style()
    {
        _git.GetTagsAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([Tag("3.4.5")]);
        _commits = [Commit("fix: x")];

        var context = await _service.PrepareAsync(_project, Ct);

        context.TagPrefix.Should().BeEmpty();
        context.DraftNotes.Should().Contain("compare/3.4.5...3.4.6");
    }

    [Fact]
    public async Task Working_state_problems_are_warnings()
    {
        _status = _status with
        {
            Branch = "feature/x",
            Upstream = "origin/feature/x",
            Ahead = 2,
            Behind = 1,
            Entries = [new GitStatusEntry { Path = "a.txt", WorkTreeState = GitFileState.Modified }],
        };
        _commits = [];

        var context = await _service.PrepareAsync(_project, Ct);

        context.Blockers.Should().BeEmpty();
        context.HasUncommittedChanges.Should().BeTrue();
        context.UnpushedCommits.Should().Be(2);
        context.Warnings.Should().BeEquivalentTo(
            "1 uncommitted change won't be part of this release. Commit it first if it belongs in it.",
            "2 commits on feature/x are not pushed. Push first so GitHub has the code you release.",
            "feature/x is 1 commit behind origin/feature/x. Pull first so the release includes the latest changes.",
            "You are releasing from feature/x, not from the default branch main.",
            "Nothing was committed since v1.1.0: the new release would contain the same code.");
    }

    [Fact]
    public async Task An_unpublished_branch_is_a_warning()
    {
        _status = _status with { Upstream = null };

        var context = await _service.PrepareAsync(_project, Ct);

        context.Warnings.Should().Contain(w => w.StartsWith("The branch main is not on GitHub yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_github_remote_and_sign_in_are_blockers()
    {
        _github.IsSignedIn.Returns(false);
        _git.GetRemotesAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([new GitRemote("origin", "https://gitlab.com/octo/forge.git", null)]);

        var context = await _service.PrepareAsync(_project with { GitHub = null }, Ct);

        context.Blockers.Should().Equal(
            "This project has no GitHub remote. Publish it to GitHub from the GitHub tab first.",
            "You're not signed in to GitHub. Sign in to publish releases.");
        context.Suggestions.Should().NotBeEmpty("the form can still be filled while the blockers are fixed");
    }

    [Fact]
    public async Task The_github_remote_is_read_from_git_when_the_project_has_none()
    {
        _git.GetRemotesAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns([
            new GitRemote("fork", "https://github.com/me/forge.git", null),
            new GitRemote("origin", "git@github.com:octo/forge.git", null),
        ]);
        _commits = [Commit("fix: x")];

        var context = await _service.PrepareAsync(_project with { GitHub = null }, Ct);

        context.Blockers.Should().BeEmpty();
        context.DraftNotes.Should().Contain("https://github.com/octo/forge/compare/");
    }

    [Fact]
    public async Task A_detached_head_is_a_blocker()
    {
        _status = _status with { Branch = null, IsDetached = true, Upstream = null };

        var context = await _service.PrepareAsync(_project, Ct);

        context.Blockers.Should().ContainSingle().Which.Should().Contain("HEAD is detached");
    }

    [Fact]
    public async Task A_repository_without_commits_is_a_blocker()
    {
        _status = new GitStatus { Branch = "main", IsUnborn = true };

        var context = await _service.PrepareAsync(_project, Ct);

        context.Blockers.Should().ContainSingle().Which.Should().Contain("no commits yet");
        await _git.DidNotReceiveWithAnyArgs().GetLogAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_a_blocker()
    {
        _git.IsRepositoryAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns(false);

        var context = await _service.PrepareAsync(_project, Ct);

        context.Blockers.Should().ContainSingle().Which.Should().Contain("not a Git repository");
        context.Suggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_git_is_a_blocker_with_the_translated_message()
    {
        _git.IsRepositoryAsync(_dir.Path, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.GitNotFound, "Git is not installed on this computer."));

        var context = await _service.PrepareAsync(_project, Ct);

        context.Blockers.Should().Equal("Git is not installed on this computer.");
    }

    [Fact]
    public async Task A_missing_project_folder_is_a_blocker()
    {
        var context = await _service.PrepareAsync(_project with { Path = Path.Combine(_dir.Path, "moved") }, Ct);

        context.Blockers.Should().ContainSingle().Which.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task Unreadable_history_becomes_a_warning()
    {
        _git.GetLogAsync(_dir.Path, Arg.Any<GitLogQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.GitCommandFailed, "git log failed."));

        var context = await _service.PrepareAsync(_project, Ct);

        context.Blockers.Should().BeEmpty();
        context.Warnings.Should().Contain("The commits since the last release could not be read: git log failed.");
    }

    [Fact]
    public async Task Recent_build_outputs_are_suggested_as_assets()
    {
        var zip = _dir.WriteFile("dist/forge.zip", "z");
        var exe = _dir.WriteFile("src/App/bin/Release/net10.0/win-x64/publish/Forge.exe", "e");
        _dir.WriteFile("dist/notes.txt", "not an artifact");
        var old = _dir.WriteFile("dist/forge-0.9.zip", "old");
        File.SetLastWriteTimeUtc(old, _clock.Now.UtcDateTime.AddDays(-30));

        var context = await _service.PrepareAsync(_project, Ct);

        context.SuggestedAssets.Should().BeEquivalentTo(zip, exe);
    }

    private static GitTag Tag(string name) => new(name, HeadSha, null, null, true);

    private static int _next;

    private static GitCommit Commit(string subject, int parents = 1) => new()
    {
        Sha = $"{Interlocked.Increment(ref _next):x7}".PadRight(40, '0'),
        Subject = subject,
        Author = new GitSignature("Dev", "dev@example.com", DateTimeOffset.UnixEpoch),
        Parents = Enumerable.Range(0, parents).Select(i => $"p{i}").ToList(),
    };
}
