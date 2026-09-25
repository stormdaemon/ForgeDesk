using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;

namespace ForgeDesk.Core.Projects;

/// <summary>Turns a snapshot into the reasons a project needs attention, most severe first.</summary>
internal static class AttentionRules
{
    // Section names match the workspace tabs so the dashboard can open the right one.
    public const string GitSection = "Git";
    public const string GitHubSection = "GitHub";
    public const string CommandsSection = "Commands";
    public const string OverviewSection = "Overview";

    /// <param name="snapshot">The snapshot being built (its own Attention is ignored).</param>
    /// <param name="status">The git status it was built from, when the folder is a repository.</param>
    /// <param name="hasRemotes">Whether the repository has at least one remote (publishing a branch is only possible then).</param>
    public static IReadOnlyList<AttentionReason> Evaluate(ProjectSnapshot snapshot, GitStatus? status, bool hasRemotes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reasons = new List<AttentionReason>();
        if (!snapshot.FolderExists)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Critical, "The project folder was moved or deleted.", OverviewSection));
            return reasons;
        }

        if (snapshot.ConflictedFiles > 0)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Critical,
                $"{Count(snapshot.ConflictedFiles, "file has", "files have")} merge conflicts.", GitSection));
        }

        if (snapshot.Ci is { State: CiState.Failure } ci)
        {
            var branch = ci.Branch ?? snapshot.Branch;
            reasons.Add(new AttentionReason(AttentionLevel.Critical,
                branch is null ? "CI is failing." : $"CI is failing on {branch}.", GitHubSection));
        }

        if (snapshot.Behind > 0)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Warning,
                $"{Count(snapshot.Behind, "commit", "commits")} behind {snapshot.Upstream ?? "the remote"}.", GitSection));
        }

        if (snapshot.IsDetachedHead)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Warning, "HEAD is detached: new commits would not belong to any branch.", GitSection));
        }

        if (status is not null && OperationInProgress(status.State) is { } operation)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Warning, $"{operation} is in progress.", GitSection));
        }

        if (snapshot.LastRun is { Succeeded: false } lastRun)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Warning, $"The last run of '{lastRun.Label}' failed.", CommandsSection));
        }

        if (snapshot.Ahead > 0)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Info,
                $"{Count(snapshot.Ahead, "commit", "commits")} not pushed.", GitSection));
        }

        var uncommitted = snapshot.ChangedFiles - snapshot.ConflictedFiles;
        if (uncommitted > 0)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Info,
                $"{Count(uncommitted, "uncommitted change", "uncommitted changes")}.", GitSection));
        }

        var hasCommits = status is { IsUnborn: false };
        if (hasRemotes && hasCommits && snapshot.Branch is { } localBranch && !snapshot.IsDetachedHead && snapshot.Upstream is null)
        {
            reasons.Add(new AttentionReason(AttentionLevel.Info, $"Branch '{localBranch}' is not published to a remote yet.", GitSection));
        }

        // Stable sort keeps the rule order within a level.
        return reasons.OrderByDescending(r => r.Level).ToList();
    }

    private static string? OperationInProgress(GitRepositoryState state) => state switch
    {
        GitRepositoryState.Merging => "A merge",
        GitRepositoryState.Rebasing => "A rebase",
        GitRepositoryState.CherryPicking => "A cherry-pick",
        GitRepositoryState.Reverting => "A revert",
        GitRepositoryState.Bisecting => "A bisect",
        _ => null,
    };

    private static string Count(int count, string singular, string plural) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural}";
}
