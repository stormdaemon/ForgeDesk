using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitServiceTagAndStashTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    [Fact]
    public async Task Tags_are_listed_newest_first_with_their_kind()
    {
        var repo = _sandbox.CreateRepository();
        _sandbox.Commit(repo.Path, "Old", T0, ("a.txt", "a\n"));
        var oldSha = repo.Git("rev-parse", "HEAD");
        await Git.CreateTagAsync(repo.Path, "v1.0", cancellationToken: Ct);
        _sandbox.Commit(repo.Path, "New", T0.AddDays(30), ("b.txt", "b\n"));
        var newSha = repo.Git("rev-parse", "HEAD");
        _sandbox.RunWith(repo.Path, new Dictionary<string, string> { ["GIT_COMMITTER_DATE"] = $"{T0.AddDays(31).ToUnixTimeSeconds()} +0000" },
            "tag", "-a", "v2.0", "-m", "Release 2.0\n\nHighlights:\n- faster");

        var tags = await Git.GetTagsAsync(repo.Path, Ct);

        tags.Select(t => t.Name).Should().Equal("v2.0", "v1.0");
        tags[0].Should().Match<GitTag>(t => t.IsAnnotated && t.TargetSha == newSha && t.Date == T0.AddDays(31));
        tags[0].Message.Should().Be("Release 2.0\n\nHighlights:\n- faster");
        tags[1].Should().Match<GitTag>(t => !t.IsAnnotated && t.TargetSha == oldSha && t.Message == null && t.Date == T0);
    }

    [Fact]
    public async Task Create_annotated_tag_on_a_target_and_delete_it()
    {
        var repo = _sandbox.CreateRepository();
        var first = repo.Git("rev-parse", "HEAD");
        repo.Commit("Second", ("b.txt", "b\n"));

        await Git.CreateTagAsync(repo.Path, "release/1.0", "Notes line 1\n#42 fixed", first, Ct);

        var tag = (await Git.GetTagsAsync(repo.Path, Ct)).Should().ContainSingle().Subject;
        tag.TargetSha.Should().Be(first);
        tag.IsAnnotated.Should().BeTrue();
        tag.Message.Should().Be("Notes line 1\n#42 fixed");

        await Git.DeleteTagAsync(repo.Path, "release/1.0", Ct);
        (await Git.GetTagsAsync(repo.Path, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tag_errors()
    {
        var repo = _sandbox.CreateRepository();
        await Git.CreateTagAsync(repo.Path, "v1", cancellationToken: Ct);

        var duplicate = () => Git.CreateTagAsync(repo.Path, "v1", cancellationToken: Ct);
        var invalid = () => Git.CreateTagAsync(repo.Path, "bad tag", cancellationToken: Ct);
        var missing = () => Git.DeleteTagAsync(repo.Path, "v9", Ct);

        (await duplicate.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
        (await invalid.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        (await missing.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Stash_round_trip_with_untracked_files()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "work in progress\n");
        repo.WriteFile("draft.txt", "draft\n");

        await Git.StashAsync(repo.Path, "WIP: settings screen", cancellationToken: Ct);

        (await Git.GetStatusAsync(repo.Path, Ct)).IsClean.Should().BeTrue();
        var stash = (await Git.GetStashesAsync(repo.Path, Ct)).Should().ContainSingle().Subject;
        stash.Index.Should().Be(0);
        stash.Name.Should().Be("stash@{0}");
        stash.Message.Should().Be("On main: WIP: settings screen");
        stash.Date.Should().NotBeNull();

        await Git.StashPopAsync(repo.Path, cancellationToken: Ct);

        File.ReadAllText(repo.Combine("README.md")).Should().Be("work in progress\n");
        File.ReadAllText(repo.Combine("draft.txt")).Should().Be("draft\n");
        (await Git.GetStashesAsync(repo.Path, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Stash_without_untracked_files_leaves_them_in_place()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "edit\n");
        repo.WriteFile("draft.txt", "draft\n");

        await Git.StashAsync(repo.Path, includeUntracked: false, cancellationToken: Ct);

        var status = await Git.GetStatusAsync(repo.Path, Ct);
        status.Entries.Should().ContainSingle(e => e.Path == "draft.txt" && e.IsUntracked);
        status.StashCount.Should().Be(1);
    }

    [Fact]
    public async Task Stash_drop_removes_the_chosen_entry()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "one\n");
        await Git.StashAsync(repo.Path, "first", cancellationToken: Ct);
        repo.WriteFile("README.md", "two\n");
        await Git.StashAsync(repo.Path, "second", cancellationToken: Ct);

        await Git.StashDropAsync(repo.Path, 1, Ct);

        (await Git.GetStashesAsync(repo.Path, Ct)).Should().ContainSingle().Which.Message.Should().EndWith("second");
    }

    [Fact]
    public async Task Nothing_to_stash_is_reported()
    {
        var repo = _sandbox.CreateRepository();

        var act = () => Git.StashAsync(repo.Path, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NothingToCommit);
    }

    [Fact]
    public async Task Popping_a_conflicting_stash_keeps_it()
    {
        var repo = _sandbox.CreateRepository();
        repo.WriteFile("README.md", "stashed version\n");
        await Git.StashAsync(repo.Path, cancellationToken: Ct);
        repo.Commit("Conflicting", ("README.md", "committed version\n"));

        var act = () => Git.StashPopAsync(repo.Path, cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.MergeConflict);
        error.Hint.Should().Contain("stash was kept");
        (await Git.GetStashesAsync(repo.Path, Ct)).Should().ContainSingle();
        (await Git.GetStatusAsync(repo.Path, Ct)).Conflicted.Should().ContainSingle();
    }

    [Fact]
    public async Task Stash_errors_for_bad_indexes()
    {
        var repo = _sandbox.CreateRepository();

        var negative = () => Git.StashDropAsync(repo.Path, -1, Ct);
        var missing = () => Git.StashPopAsync(repo.Path, 3, Ct);

        (await negative.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        (await missing.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    public void Dispose() => _sandbox.Dispose();
}
