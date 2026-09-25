using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.Tests.Projects;

public class AttentionRulesTests
{
    private static readonly GitStatus CleanStatus = new() { Branch = "main", HeadSha = "abc", Upstream = "origin/main" };
    private static readonly ProjectSnapshot Clean = new() { ProjectId = "p", IsGitRepository = true, Branch = "main", Upstream = "origin/main" };

    [Fact]
    public void Clean_repository_needs_no_attention()
    {
        AttentionRules.Evaluate(Clean, CleanStatus, hasRemotes: true).Should().BeEmpty();
    }

    [Fact]
    public void Missing_folder_is_critical_and_the_only_reason()
    {
        var reasons = AttentionRules.Evaluate(Clean with { FolderExists = false, Behind = 3 }, null, hasRemotes: true);

        reasons.Should().ContainSingle().Which.Should().Be(new AttentionReason(AttentionLevel.Critical, "The project folder was moved or deleted.", "Overview"));
    }

    [Fact]
    public void Merge_conflicts_are_critical()
    {
        var reasons = AttentionRules.Evaluate(Clean with { ConflictedFiles = 2, ChangedFiles = 2 }, CleanStatus with { State = GitRepositoryState.Merging }, true);

        reasons.Should().Equal(
            new AttentionReason(AttentionLevel.Critical, "2 files have merge conflicts.", "Git"),
            new AttentionReason(AttentionLevel.Warning, "A merge is in progress.", "Git"));
    }

    [Fact]
    public void Failing_ci_is_critical()
    {
        var reasons = AttentionRules.Evaluate(Clean with { Ci = new CiSummary { State = CiState.Failure, Branch = "main" } }, CleanStatus, true);

        reasons.Should().ContainSingle().Which.Should().Be(new AttentionReason(AttentionLevel.Critical, "CI is failing on main.", "GitHub"));
    }

    [Theory]
    [InlineData(CiState.Success)]
    [InlineData(CiState.Unknown)]
    [InlineData(CiState.Running)]
    public void Other_ci_states_need_no_attention(CiState state)
    {
        AttentionRules.Evaluate(Clean with { Ci = new CiSummary { State = state } }, CleanStatus, true).Should().BeEmpty();
    }

    [Fact]
    public void Behind_remote_is_a_warning()
    {
        var reasons = AttentionRules.Evaluate(Clean with { Behind = 1 }, CleanStatus, true);

        reasons.Should().ContainSingle().Which.Should().Be(new AttentionReason(AttentionLevel.Warning, "1 commit behind origin/main.", "Git"));
    }

    [Fact]
    public void Detached_head_is_a_warning()
    {
        var reasons = AttentionRules.Evaluate(Clean with { Branch = null, Upstream = null, IsDetachedHead = true }, CleanStatus with { Branch = null, IsDetached = true }, true);

        reasons.Should().ContainSingle().Which.Level.Should().Be(AttentionLevel.Warning);
        reasons[0].Message.Should().Contain("detached");
    }

    [Theory]
    [InlineData(GitRepositoryState.Rebasing, "A rebase is in progress.")]
    [InlineData(GitRepositoryState.CherryPicking, "A cherry-pick is in progress.")]
    [InlineData(GitRepositoryState.Reverting, "A revert is in progress.")]
    [InlineData(GitRepositoryState.Bisecting, "A bisect is in progress.")]
    public void Operations_in_progress_are_warnings(GitRepositoryState state, string message)
    {
        var reasons = AttentionRules.Evaluate(Clean, CleanStatus with { State = state }, true);

        reasons.Should().ContainSingle().Which.Should().Be(new AttentionReason(AttentionLevel.Warning, message, "Git"));
    }

    [Fact]
    public void Failed_last_run_is_a_warning_in_commands()
    {
        var reasons = AttentionRules.Evaluate(Clean with { LastRun = new RunOutcomeSummary("npm test", false, DateTimeOffset.UnixEpoch) }, CleanStatus, true);

        reasons.Should().ContainSingle().Which.Should().Be(new AttentionReason(AttentionLevel.Warning, "The last run of 'npm test' failed.", "Commands"));
    }

    [Fact]
    public void Unpushed_and_uncommitted_work_is_informational()
    {
        var reasons = AttentionRules.Evaluate(Clean with { Ahead = 3, ChangedFiles = 1 }, CleanStatus, true);

        reasons.Should().Equal(
            new AttentionReason(AttentionLevel.Info, "3 commits not pushed.", "Git"),
            new AttentionReason(AttentionLevel.Info, "1 uncommitted change.", "Git"));
    }

    [Fact]
    public void Unpublished_branch_is_informational_only_when_there_is_a_remote_and_commits()
    {
        var local = Clean with { Branch = "feature/x", Upstream = null };
        var status = CleanStatus with { Branch = "feature/x", Upstream = null };

        AttentionRules.Evaluate(local, status, hasRemotes: true).Should().ContainSingle()
            .Which.Should().Be(new AttentionReason(AttentionLevel.Info, "Branch 'feature/x' is not published to a remote yet.", "Git"));
        AttentionRules.Evaluate(local, status, hasRemotes: false).Should().BeEmpty();
        AttentionRules.Evaluate(local, status with { IsUnborn = true }, hasRemotes: true).Should().BeEmpty();
    }

    [Fact]
    public void Reasons_are_ordered_by_severity()
    {
        var snapshot = Clean with { Ahead = 1, Behind = 1, ConflictedFiles = 1, ChangedFiles = 3, LastRun = new RunOutcomeSummary("Build", false, DateTimeOffset.UnixEpoch) };

        var reasons = AttentionRules.Evaluate(snapshot, CleanStatus, true);

        reasons.Select(r => r.Level).Should().BeInDescendingOrder();
        reasons.Select(r => r.Level).Should().Equal(AttentionLevel.Critical, AttentionLevel.Warning, AttentionLevel.Warning, AttentionLevel.Info, AttentionLevel.Info);
        (snapshot with { Attention = reasons }).AttentionLevel.Should().Be(AttentionLevel.Critical);
        reasons.Last().Message.Should().Be("2 uncommitted changes.");
    }
}
