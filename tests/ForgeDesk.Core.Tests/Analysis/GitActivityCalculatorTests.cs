using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Analysis;

public class GitActivityCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Counts_recent_commits_and_buckets_them_by_week_oldest_first()
    {
        var commits = new[]
        {
            Commit(daysAgo: 0.5), Commit(daysAgo: 6.9), Commit(daysAgo: 7.1), Commit(daysAgo: 29),
            Commit(daysAgo: 31), Commit(daysAgo: 83), Commit(daysAgo: 85), Commit(daysAgo: 400),
        };

        var activity = GitActivityCalculator.Compute(commits, Now);

        activity.TotalCommits.Should().Be(8);
        activity.CommitsLast30Days.Should().Be(4);
        activity.CommitsLast90Days.Should().Be(7);
        activity.WeeklyCommits.Should().Equal(1, 0, 0, 0, 0, 0, 0, 2, 0, 0, 1, 2);
        activity.FirstCommitAt.Should().Be(Now.AddDays(-400));
        activity.LastCommitAt.Should().Be(Now.AddDays(-0.5));
    }

    [Fact]
    public void Future_dated_commits_count_as_this_week()
    {
        var activity = GitActivityCalculator.Compute([Commit(daysAgo: -2)], Now);

        activity.WeeklyCommits[^1].Should().Be(1);
        activity.CommitsLast30Days.Should().Be(1);
    }

    [Fact]
    public void An_empty_history_has_twelve_empty_weeks()
    {
        var activity = GitActivityCalculator.Compute([], Now);

        activity.WeeklyCommits.Should().HaveCount(12).And.OnlyContain(c => c == 0);
        activity.FirstCommitAt.Should().BeNull();
        activity.TopContributors.Should().BeEmpty();
    }

    [Fact]
    public void Contributors_sharing_a_name_or_email_are_merged_case_insensitively()
    {
        var commits = new[]
        {
            Commit(1, "Ana Lima", "ana@home.dev"),
            Commit(1, "ana lima", "ANA@HOME.DEV"),
            Commit(1, "Ana Lima", "ana@work.com"),
            Commit(1, "A. Lima", "ana@work.com"),
            Commit(1, "Bob", "bob@x.dev"),
            Commit(1, "Bob", "bob@x.dev"),
            Commit(1, "Chen", "chen@x.dev"),
            Commit(1, "Dee", "dee@x.dev"),
            Commit(1, "Eve", "eve@x.dev"),
            Commit(1, "Finn", "finn@x.dev"),
            Commit(1, "", "ghost@x.dev"),
        };

        var top = GitActivityCalculator.TopContributors(commits, 5);

        top.Should().Equal(
            new ContributorStat("Ana Lima", 4),
            new ContributorStat("Bob", 2),
            new ContributorStat("Chen", 1),
            new ContributorStat("Dee", 1),
            new ContributorStat("Eve", 1));
        GitActivityCalculator.TopContributors(commits, 20).Should().Contain(new ContributorStat("ghost@x.dev", 1));
    }

    private static GitCommit Commit(double daysAgo, string name = "Dev", string email = "dev@x.dev") => new()
    {
        Sha = Guid.NewGuid().ToString("N"),
        Subject = "change",
        Author = new GitSignature(name, email, Now.AddDays(-daysAgo)),
    };
}
