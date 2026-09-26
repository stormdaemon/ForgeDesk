using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.Tests.Analysis;

public class HealthScorerTests
{
    private static readonly IReadOnlyList<ImportantFileFinding> AllPresent = Checklist(present: true);

    [Fact]
    public void A_healthy_project_scores_100_without_attention()
    {
        var result = HealthScorer.Evaluate(Healthy());

        result.Score.Should().Be(100);
        result.Attention.Should().BeEmpty();
    }

    [Fact]
    public void Missing_essentials_are_flagged_with_their_documented_weights()
    {
        var result = HealthScorer.Evaluate(Healthy() with { ImportantFiles = Checklist(present: false), HasTests = false });

        result.Score.Should().Be(100
            - HealthScorer.MissingReadmePenalty - HealthScorer.MissingTestsPenalty - HealthScorer.MissingLicensePenalty
            - HealthScorer.MissingCiPenalty - HealthScorer.MissingGitIgnorePenalty - HealthScorer.MissingLockfilePenalty
            - (4 * HealthScorer.MissingNiceToHavePenalty));
        result.Attention.Select(a => (a.Level, a.Section)).Should().Equal(
            (AttentionLevel.Warning, "Files"),
            (AttentionLevel.Warning, "Insights"),
            (AttentionLevel.Warning, "Files"),
            (AttentionLevel.Info, "Files"),
            (AttentionLevel.Info, "Files"),
            (AttentionLevel.Info, "Commands"));
        result.Attention.Should().Contain(a => a.Message.StartsWith("Add a README", StringComparison.Ordinal));
        result.Attention.Should().Contain(a => a.Message == "No npm lockfile. Run the install command and commit the lockfile so every install gets the same versions.");
    }

    [Fact]
    public void Gitignore_is_only_expected_in_repositories()
    {
        var checklist = AllPresent.Select(f => f.Kind == ImportantFileKind.GitIgnore ? f with { Check = f.Check with { Present = false } } : f).ToList();

        HealthScorer.Evaluate(Healthy() with { ImportantFiles = checklist, Repository = null }).Attention.Should().BeEmpty();
        HealthScorer.Evaluate(Healthy() with { ImportantFiles = checklist }).Attention.Should().ContainSingle(a => a.Message.Contains(".gitignore"));
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(21, HealthScorer.ManyTodosPenalty)]
    [InlineData(101, HealthScorer.VeryManyTodosPenalty)]
    public void Many_todos_cost_points(int todos, int penalty)
    {
        var result = HealthScorer.Evaluate(Healthy() with { TodoCount = todos });

        result.Score.Should().Be(100 - penalty);
        if (penalty > 0)
        {
            result.Attention.Should().ContainSingle().Which.Should().Match<AttentionReason>(a =>
                a.Section == "Insights" && a.Message.StartsWith($"{todos} TODO/FIXME markers", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Risky_repository_state_is_flagged_in_the_git_section()
    {
        var repository = new RepositoryState
        {
            UncommittedChanges = 120,
            UnpushedCommits = 3,
            BehindCommits = 2,
            Stashes = 4,
            HasUpstream = true,
            StaleBranches = ["old-a", "old-b", "old-c", "old-d", "old-e"],
            MergedBranches = ["done-1"],
        };

        var result = HealthScorer.Evaluate(Healthy() with { Repository = repository, Upstream = "origin/main" });

        result.Attention.Should().OnlyContain(a => a.Section == "Git");
        result.Attention.Select(a => a.Message).Should().Equal(
            "120 uncommitted changes. Commit them in smaller steps so work is not lost.",
            "3 commits are not pushed to origin/main yet. Push to back up and share your work.",
            "Your branch is 2 commits behind origin/main. Pull to get the latest changes.",
            "5 local branches without commits for 90+ days (old-a, old-b, old-c, …). Delete the ones you no longer need.",
            "1 branch already merged into main (done-1). Delete it to keep the branch list short.",
            "4 stashes are piling up. Apply or drop the ones you no longer need.");
        result.Score.Should().Be(100 - HealthScorer.HugeUncommittedPenalty - HealthScorer.UnpushedPenalty - HealthScorer.BehindPenalty
            - HealthScorer.MaxStaleBranchPenalty - HealthScorer.MergedBranchPenalty - HealthScorer.StashesPenalty);
    }

    [Fact]
    public void Unpublished_work_is_flagged()
    {
        var noRemote = HealthScorer.Evaluate(Healthy() with { HasRemote = false });
        noRemote.Attention.Should().ContainSingle().Which.Message.Should().StartWith("This repository has no remote.");

        var unpublishedBranch = HealthScorer.Evaluate(Healthy() with { Repository = new RepositoryState { HasUpstream = false }, CurrentBranch = "feature/x" });
        unpublishedBranch.Attention.Should().ContainSingle().Which.Message.Should().Be("The branch feature/x is not on the remote yet. Push it to back it up and share it.");
        unpublishedBranch.Score.Should().Be(100 - HealthScorer.UnpublishedPenalty);
    }

    [Fact]
    public void Oversized_files_are_warned_about_with_a_capped_penalty()
    {
        var files = Enumerable.Range(0, 4).Select(i => new LargeFile($"assets/video{i}.mp4", 52L * 1024 * 1024)).ToList();

        var result = HealthScorer.Evaluate(Healthy() with { OversizedFiles = files });

        result.Attention.Should().HaveCount(2).And.OnlyContain(a => a.Level == AttentionLevel.Warning && a.Section == "Files");
        result.Attention[0].Message.Should().Be("assets/video0.mp4 (52 MB) is in the repository. Move it to Git LFS or out of Git to keep clones fast.");
        result.Score.Should().Be(100 - HealthScorer.MaxOversizedFilePenalty);
    }

    [Fact]
    public void The_score_never_goes_below_zero()
    {
        var result = HealthScorer.Evaluate(new HealthSignals
        {
            ImportantFiles = Checklist(present: false),
            TodoCount = 1000,
            HasRemote = false,
            Repository = new RepositoryState
            {
                UncommittedChanges = 500,
                UnpushedCommits = 50,
                BehindCommits = 9,
                Stashes = 9,
                StaleBranches = ["a", "b", "c", "d", "e"],
                MergedBranches = ["f", "g", "h", "i", "j", "k"],
            },
            OversizedFiles = [new LargeFile("a.bin", 1L << 30), new LargeFile("b.bin", 1L << 30)],
        });

        result.Score.Should().Be(0);
        result.Attention.Should().BeInDescendingOrder(a => a.Level);
    }

    private static HealthSignals Healthy() => new()
    {
        ImportantFiles = AllPresent,
        HasTests = true,
        TodoCount = 3,
        Repository = new RepositoryState { HasUpstream = true, LocalBranches = 1 },
        CurrentBranch = "main",
        Upstream = "origin/main",
        DefaultBranch = "main",
        HasRemote = true,
    };

    private static List<ImportantFileFinding> Checklist(bool present) =>
        Enum.GetValues<ImportantFileKind>()
            .Where(k => k != ImportantFileKind.Tests)
            .Select(k => new ImportantFileFinding(k, new ImportantFileCheck(k.ToString(), present, present ? k.ToString() : null, "why"), k == ImportantFileKind.Lockfile ? "npm" : null))
            .ToList();
}
