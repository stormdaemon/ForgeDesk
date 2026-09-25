using System.Globalization;
using ForgeDesk.Core.Projects;

namespace ForgeDesk.Core.Analysis;

/// <summary>What the scorer looks at, gathered by the analyzer.</summary>
internal sealed record HealthSignals
{
    public IReadOnlyList<ImportantFileFinding> ImportantFiles { get; init; } = [];
    public bool HasTests { get; init; }
    public int TodoCount { get; init; }

    /// <summary>Null when the project is not a Git repository or Git could not be used.</summary>
    public RepositoryState? Repository { get; init; }
    public string? CurrentBranch { get; init; }
    public string? Upstream { get; init; }
    public string? DefaultBranch { get; init; }
    public bool HasRemote { get; init; }

    /// <summary>Files in the repository larger than <see cref="AnalysisLimits.OversizedFileBytes"/>.</summary>
    public IReadOnlyList<LargeFile> OversizedFiles { get; init; } = [];
}

internal sealed record HealthAssessment(IReadOnlyList<AttentionReason> Attention, int Score);

/// <summary>
/// Turns signals into actionable attention reasons and a 0–100 score. The score starts at 100
/// and each finding subtracts a fixed penalty (see the constants), so it is easy to explain:
/// the essentials (README, tests, license, CI) weigh most, then risks to unshared work
/// (uncommitted or unpushed changes), then housekeeping (branches, stashes, TODOs).
/// Checklist-only items (CONTRIBUTING, CHANGELOG, SECURITY, .editorconfig) cost a little but
/// raise no attention reason: the checklist already shows them.
/// </summary>
internal static class HealthScorer
{
    internal const int MissingReadmePenalty = 15;
    internal const int MissingTestsPenalty = 15;
    internal const int MissingLicensePenalty = 10;
    internal const int MissingCiPenalty = 10;
    internal const int MissingGitIgnorePenalty = 5;
    internal const int MissingLockfilePenalty = 5;
    internal const int MissingNiceToHavePenalty = 2;
    internal const int ManyTodosPenalty = 5;
    internal const int VeryManyTodosPenalty = 10;
    internal const int LargeUncommittedPenalty = 5;
    internal const int HugeUncommittedPenalty = 10;
    internal const int UnpushedPenalty = 5;
    internal const int ManyUnpushedPenalty = 10;
    internal const int BehindPenalty = 3;
    internal const int UnpublishedPenalty = 5;
    internal const int StaleBranchPenalty = 2;
    internal const int MaxStaleBranchPenalty = 8;
    internal const int MergedBranchPenalty = 1;
    internal const int MaxMergedBranchPenalty = 5;
    internal const int StashesPenalty = 3;
    internal const int OversizedFilePenalty = 5;
    internal const int MaxOversizedFilePenalty = 10;

    internal const int ManyTodos = 20;
    internal const int VeryManyTodos = 100;
    internal const int LargeUncommitted = 20;
    internal const int HugeUncommitted = 100;
    internal const int ManyUnpushed = 20;
    internal const int ManyStashes = 3;

    private const string Git = "Git";
    private const string Insights = "Insights";
    private const string Files = "Files";
    private const string Commands = "Commands";

    public static HealthAssessment Evaluate(HealthSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var reasons = new List<AttentionReason>();
        var penalty = 0;

        void Flag(int cost, AttentionLevel level, string message, string section)
        {
            penalty += cost;
            reasons.Add(new AttentionReason(level, message, section));
        }

        bool Missing(ImportantFileKind kind) => signals.ImportantFiles.Any(f => f.Kind == kind && !f.Check.Present);

        if (Missing(ImportantFileKind.Readme))
        {
            Flag(MissingReadmePenalty, AttentionLevel.Warning,
                "Add a README that explains what the project does and how to run it.", Files);
        }

        if (!signals.HasTests)
        {
            Flag(MissingTestsPenalty, AttentionLevel.Warning,
                "No tests found. Add a first test for the most important behavior to catch regressions early.", Insights);
        }

        if (Missing(ImportantFileKind.License))
        {
            Flag(MissingLicensePenalty, AttentionLevel.Info,
                "Add a LICENSE file: without one, nobody else may legally use or contribute to the code.", Files);
        }

        if (Missing(ImportantFileKind.CiWorkflow))
        {
            Flag(MissingCiPenalty, AttentionLevel.Info,
                "Set up CI (for example .github/workflows/ci.yml) to build and test every push automatically.", Files);
        }

        if (signals.Repository is not null && Missing(ImportantFileKind.GitIgnore))
        {
            Flag(MissingGitIgnorePenalty, AttentionLevel.Warning,
                "Add a .gitignore so build output, dependencies and secrets stay out of Git.", Files);
        }

        foreach (var lockfile in signals.ImportantFiles.Where(f => f.Kind == ImportantFileKind.Lockfile && !f.Check.Present))
        {
            Flag(MissingLockfilePenalty, AttentionLevel.Info,
                $"No {lockfile.Ecosystem} lockfile. Run the install command and commit the lockfile so every install gets the same versions.", Commands);
        }

        penalty += MissingNiceToHavePenalty * signals.ImportantFiles.Count(f =>
            f.Kind is ImportantFileKind.Contributing or ImportantFileKind.Changelog or ImportantFileKind.Security or ImportantFileKind.EditorConfig
            && !f.Check.Present);

        if (signals.TodoCount > ManyTodos)
        {
            Flag(signals.TodoCount > VeryManyTodos ? VeryManyTodosPenalty : ManyTodosPenalty, AttentionLevel.Info,
                $"{Count(signals.TodoCount)} TODO/FIXME markers in the code. Review them and turn the important ones into tasks.", Insights);
        }

        foreach (var file in signals.OversizedFiles.Take(MaxOversizedFilePenalty / OversizedFilePenalty))
        {
            Flag(OversizedFilePenalty, AttentionLevel.Warning,
                $"{file.RelativePath} ({Common.PathUtil.FormatBytes(file.Bytes)}) is in the repository. Move it to Git LFS or out of Git to keep clones fast.", Files);
        }

        if (signals.Repository is { } repository)
        {
            EvaluateRepository(signals, repository, Flag);
        }

        return new HealthAssessment(
            reasons.OrderByDescending(r => r.Level).ToList(),
            Math.Clamp(100 - penalty, 0, 100));
    }

    private static void EvaluateRepository(HealthSignals signals, RepositoryState repository, Action<int, AttentionLevel, string, string> flag)
    {
        if (repository.UncommittedChanges >= LargeUncommitted)
        {
            flag(repository.UncommittedChanges >= HugeUncommitted ? HugeUncommittedPenalty : LargeUncommittedPenalty, AttentionLevel.Warning,
                $"{Count(repository.UncommittedChanges)} uncommitted changes. Commit them in smaller steps so work is not lost.", Git);
        }

        if (repository.UnpushedCommits > 0)
        {
            var target = signals.Upstream is null ? "the remote" : signals.Upstream;
            flag(repository.UnpushedCommits >= ManyUnpushed ? ManyUnpushedPenalty : UnpushedPenalty, AttentionLevel.Warning,
                $"{Plural(repository.UnpushedCommits, "commit")} {(repository.UnpushedCommits == 1 ? "is" : "are")} not pushed to {target} yet. Push to back up and share your work.", Git);
        }

        if (repository.BehindCommits > 0)
        {
            flag(BehindPenalty, AttentionLevel.Info,
                $"Your branch is {Plural(repository.BehindCommits, "commit")} behind {signals.Upstream ?? "its upstream"}. Pull to get the latest changes.", Git);
        }

        if (!signals.HasRemote)
        {
            flag(UnpublishedPenalty, AttentionLevel.Info,
                "This repository has no remote. Publish it (for example to GitHub) so the code is backed up.", Git);
        }
        else if (!repository.HasUpstream && signals.CurrentBranch is { } branch)
        {
            flag(UnpublishedPenalty, AttentionLevel.Info,
                $"The branch {branch} is not on the remote yet. Push it to back it up and share it.", Git);
        }

        if (repository.StaleBranches.Count > 0)
        {
            flag(Math.Min(MaxStaleBranchPenalty, StaleBranchPenalty * repository.StaleBranches.Count), AttentionLevel.Info,
                $"{Plural(repository.StaleBranches.Count, "local branch", "local branches")} without commits for 90+ days ({List(repository.StaleBranches)}). Delete the ones you no longer need.", Git);
        }

        if (repository.MergedBranches.Count > 0)
        {
            var into = signals.DefaultBranch is null ? "the default branch" : signals.DefaultBranch;
            flag(Math.Min(MaxMergedBranchPenalty, MergedBranchPenalty * repository.MergedBranches.Count), AttentionLevel.Info,
                $"{Plural(repository.MergedBranches.Count, "branch", "branches")} already merged into {into} ({List(repository.MergedBranches)}). Delete {(repository.MergedBranches.Count == 1 ? "it" : "them")} to keep the branch list short.", Git);
        }

        if (repository.Stashes >= ManyStashes)
        {
            flag(StashesPenalty, AttentionLevel.Info,
                $"{Count(repository.Stashes)} stashes are piling up. Apply or drop the ones you no longer need.", Git);
        }
    }

    private static string List(IReadOnlyList<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + ", …";

    private static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    private static string Plural(int count, string singular, string? plural = null) =>
        $"{Count(count)} {(count == 1 ? singular : plural ?? singular + "s")}";
}
