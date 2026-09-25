using ForgeDesk.Core.Git;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;

namespace ForgeDesk.Core.Tests.Releases;

public class ReleaseNotesBuilderTests
{
    private static readonly GitHubRepoRef Repo = new("octo", "forge");
    private static int _sequence;

    [Fact]
    public void Groups_commits_by_type_in_a_fixed_section_order()
    {
        var notes = ReleaseNotesBuilder.Build(
        [
            Commit("chore: bump dependencies", "c000001"),
            Commit("fix(git): handle detached HEAD", "f000001"),
            Commit("feat: add dark mode", "f000002"),
            Commit("docs: explain release flow", "d000001"),
            Commit("perf: cache file index", "p000001"),
            Commit("Update screenshots", "o000001"),
            Commit("feat(ui)!: redesign the sidebar", "b000001"),
        ], Repo, "v1.0.0", "v2.0.0");

        notes.Should().Be(
            """
            ### ⚠️ Breaking changes
            - **ui:** Redesign the sidebar (b000001)

            ### ✨ Features
            - Add dark mode (f000002)

            ### 🐛 Fixes
            - **git:** Handle detached HEAD (f000001)

            ### ⚡ Performance
            - Cache file index (p000001)

            ### 📝 Documentation
            - Explain release flow (d000001)

            ### 🔧 Maintenance
            - Bump dependencies (c000001)

            ### Other changes
            - Update screenshots (o000001)

            **Full changelog**: https://github.com/octo/forge/compare/v1.0.0...v2.0.0

            """.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("refactor: split service")]
    [InlineData("chore(deps): bump x")]
    [InlineData("build: use .NET 10")]
    [InlineData("ci: cache NuGet")]
    [InlineData("test: cover edge cases")]
    [InlineData("style: format")]
    public void Maintenance_types_share_one_section(string subject)
    {
        var notes = ReleaseNotesBuilder.Build([Commit(subject)]);

        notes.Should().StartWith("### 🔧 Maintenance\n");
    }

    [Fact]
    public void A_breaking_change_footer_moves_the_commit_to_breaking_changes_only()
    {
        var notes = ReleaseNotesBuilder.Build([Commit("feat: new config format", body: "Details.\n\nBREAKING CHANGE: the old settings file is ignored")]);

        notes.Should().Contain("### ⚠️ Breaking changes\n- New config format");
        notes.Should().NotContain("Features");
    }

    [Fact]
    public void Merge_commits_are_skipped()
    {
        var notes = ReleaseNotesBuilder.Build(
        [
            Commit("Merge pull request #12 from octo/feature"),
            Commit("Merge branch 'main' into feature"),
            Commit("Squashed work", parents: 2),
            Commit("fix: real change", "e000001"),
        ]);

        notes.Should().Be("### 🐛 Fixes\n- Real change (e000001)\n");
    }

    [Fact]
    public void Unknown_prefixes_are_kept_verbatim()
    {
        var notes = ReleaseNotesBuilder.Build([Commit("README: fix typo"), Commit("WIP: something")]);

        notes.Should().Contain("- README: fix typo").And.Contain("- WIP: something").And.StartWith("### Other changes");
    }

    [Fact]
    public void Types_and_scopes_are_parsed_case_insensitively_and_trimmed()
    {
        var notes = ReleaseNotesBuilder.Build([Commit("Feat( api ):   Add pagination")]);

        notes.Should().Contain("### ✨ Features\n- **api:** Add pagination");
    }

    [Fact]
    public void Empty_notes_explain_why()
    {
        ReleaseNotesBuilder.Build([], Repo, "v1.0.0", "v1.0.1").Should().StartWith("No changes since v1.0.0.");
        ReleaseNotesBuilder.Build([]).Should().Be("First release.\n");
        ReleaseNotesBuilder.Build([Commit("Merge branch 'x'")], null, "v3.0.0").Should().Be("No changes since v3.0.0.\n");
    }

    [Theory]
    [InlineData(false, "v1.0.0", "v1.1.0")]
    [InlineData(true, null, "v1.1.0")]
    [InlineData(true, "v1.0.0", null)]
    public void Full_changelog_link_needs_a_repository_and_both_tags(bool withRepo, string? previous, string? next)
    {
        var notes = ReleaseNotesBuilder.Build([Commit("fix: x")], withRepo ? Repo : null, previous, next);

        notes.Should().NotContain("Full changelog");
    }

    [Fact]
    public void ConventionalCommit_parses_the_header()
    {
        ConventionalCommit.Parse("fix(parser)!: reject empty input").Should().Be(new ConventionalCommit("fix", "parser", "reject empty input", true));
        ConventionalCommit.Parse("feat: x").Category.Should().Be(ChangeCategory.Feature);
        ConventionalCommit.Parse("feat(): x").Scope.Should().BeNull();
        ConventionalCommit.Parse("no colon here").Should().Be(new ConventionalCommit(null, null, "no colon here", false));
        ConventionalCommit.Parse("fix:").Category.Should().Be(ChangeCategory.Other, "a type without description is not conventional");
        ConventionalCommit.Parse("docs: x", "BREAKING-CHANGE: y").IsBreaking.Should().BeTrue();
    }

    private static GitCommit Commit(string subject, string? sha = null, string body = "", int parents = 1)
    {
        var id = sha ?? $"a{Interlocked.Increment(ref _sequence):000000}";
        return new GitCommit
        {
            Sha = id + new string('0', 40 - id.Length),
            Subject = subject,
            Body = body,
            Author = new GitSignature("Dev", "dev@example.com", DateTimeOffset.UnixEpoch),
            Parents = Enumerable.Range(0, parents).Select(i => $"parent{i}").ToList(),
        };
    }
}
