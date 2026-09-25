namespace ForgeDesk.Core.GitHub;

/// <summary>Maps GitHub Actions / checks / commit-status values to <see cref="CiState"/> and aggregates them.</summary>
internal static class CiStates
{
    private const int MaxNamesInMessage = 3;

    /// <summary>Workflow runs, jobs, steps and check runs share the same status/conclusion vocabulary.</summary>
    public static CiState FromStatus(string? status, string? conclusion) => status?.ToLowerInvariant() switch
    {
        "queued" or "waiting" or "requested" or "pending" => CiState.Queued,
        "in_progress" => CiState.Running,
        "completed" => FromConclusion(conclusion),
        null or "" when !string.IsNullOrEmpty(conclusion) => FromConclusion(conclusion),
        _ => CiState.Unknown,
    };

    public static CiState FromConclusion(string? conclusion) => conclusion?.ToLowerInvariant() switch
    {
        "success" or "neutral" or "skipped" => CiState.Success,
        "failure" or "timed_out" or "startup_failure" or "action_required" => CiState.Failure,
        "cancelled" or "stale" => CiState.Cancelled,
        _ => CiState.Unknown,
    };

    /// <summary>Legacy commit statuses (external CI services): pending, success, failure, error.</summary>
    public static CiState FromCommitStatus(string? state) => state?.ToLowerInvariant() switch
    {
        "success" => CiState.Success,
        "failure" or "error" => CiState.Failure,
        "pending" => CiState.Running,
        _ => CiState.Unknown,
    };

    /// <summary>
    /// Combined state of the checks on one commit, with GitHub's precedence: a failed check
    /// already decides the outcome (it blocks the merge) even while others still run.
    /// </summary>
    public static CiState CombineChecks(IEnumerable<CiState> states)
    {
        var all = states.ToList();
        if (all.Count == 0)
        {
            return CiState.None;
        }

        if (all.Contains(CiState.Failure))
        {
            return CiState.Failure;
        }

        if (all.Contains(CiState.Running))
        {
            return CiState.Running;
        }

        if (all.Contains(CiState.Queued))
        {
            return CiState.Queued;
        }

        if (all.Contains(CiState.Cancelled))
        {
            return CiState.Cancelled;
        }

        return all.Contains(CiState.Success) ? CiState.Success : CiState.Unknown;
    }

    /// <summary>
    /// Summarizes a branch from its recent runs: only the latest run of each workflow counts.
    /// Work in progress wins over failures because the running workflows will replace the
    /// current results shortly.
    /// </summary>
    public static CiSummary Summarize(string? branch, IEnumerable<WorkflowRunInfo> recentRuns)
    {
        var latest = recentRuns
            .GroupBy(r => r.WorkflowId != 0 ? r.WorkflowId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "name:" + r.Name, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunNumber).ThenByDescending(r => r.RunAttempt).First())
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (latest.Count == 0)
        {
            return new CiSummary
            {
                State = CiState.None,
                Branch = branch,
                Message = branch is null ? "No workflow runs yet" : $"No workflow runs on '{branch}' yet",
            };
        }

        var state = Aggregate(latest.Select(r => r.State));
        var newest = latest.MaxBy(r => r.CreatedAt)!;
        return new CiSummary
        {
            State = state,
            Branch = branch,
            HeadSha = newest.HeadSha,
            UpdatedAt = latest.Max(r => r.UpdatedAt ?? r.CreatedAt),
            LatestRuns = latest,
            Message = Describe(state, latest),
        };
    }

    public static CiState Aggregate(IEnumerable<CiState> states)
    {
        var all = states.ToList();
        if (all.Count == 0)
        {
            return CiState.None;
        }

        if (all.Any(s => s is CiState.Running or CiState.Queued))
        {
            return CiState.Running;
        }

        if (all.Contains(CiState.Failure))
        {
            return CiState.Failure;
        }

        if (all.Contains(CiState.Success))
        {
            return CiState.Success;
        }

        return all.Contains(CiState.Cancelled) ? CiState.Cancelled : CiState.Unknown;
    }

    private static string Describe(CiState state, IReadOnlyList<WorkflowRunInfo> latest)
    {
        switch (state)
        {
            case CiState.Running:
                var active = latest.Count(r => r.State is CiState.Running or CiState.Queued);
                var failing = latest.Count(r => r.State == CiState.Failure);
                var running = $"{Workflows(active)} running";
                return failing > 0 ? $"{running}, {failing} failing" : running;

            case CiState.Failure:
                return $"CI failing: {Names(latest.Where(r => r.State == CiState.Failure))}";

            case CiState.Success:
                return $"{Workflows(latest.Count(r => r.State == CiState.Success))} passing";

            case CiState.Cancelled:
                return $"CI cancelled: {Names(latest.Where(r => r.State == CiState.Cancelled))}";

            default:
                return "CI status unknown";
        }
    }

    private static string Workflows(int count) => count == 1 ? "1 workflow" : $"{count} workflows";

    private static string Names(IEnumerable<WorkflowRunInfo> runs)
    {
        var names = runs.Select(r => r.Name).ToList();
        var shown = string.Join(", ", names.Take(MaxNamesInMessage));
        return names.Count > MaxNamesInMessage ? $"{shown} +{names.Count - MaxNamesInMessage} more" : shown;
    }
}
