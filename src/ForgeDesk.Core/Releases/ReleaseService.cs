using System.Globalization;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Releases;

/// <summary>
/// Prepares and publishes GitHub releases: tag → push → release (draft while assets upload) →
/// assets → publish. A failed run leaves the draft on GitHub; running the same plan again resumes it.
/// </summary>
internal sealed class ReleaseService : IReleaseService
{
    internal const int MaxCommits = 1000;

    /// <summary>GitHub rejects release assets of 2 GiB or more.</summary>
    internal const long MaxAssetBytes = (2L * 1024 * 1024 * 1024) - 1;

    private const string UrlRefKind = "url";

    private readonly IGitService _git;
    private readonly IGitHubService _github;
    private readonly IActivityLog _activity;
    private readonly IClock _clock;
    private readonly ILogger<ReleaseService> _logger;

    public ReleaseService(IGitService git, IGitHubService github, IActivityLog activity, IClock clock, ILogger<ReleaseService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(clock);
        _git = git;
        _github = github;
        _activity = activity;
        _clock = clock;
        _logger = logger ?? NullLogger<ReleaseService>.Instance;
    }

    public async Task<ReleaseContext> PrepareAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (!Directory.Exists(project.Path))
        {
            blockers.Add("The project folder no longer exists. Relocate the project, then try again.");
            return new ReleaseContext { Blockers = blockers };
        }

        GitStatus status;
        try
        {
            if (!await _git.IsRepositoryAsync(project.Path, cancellationToken).ConfigureAwait(false))
            {
                blockers.Add("This project is not a Git repository. Initialize one from the Git tab before releasing.");
                return new ReleaseContext { Blockers = blockers };
            }

            status = await _git.GetStatusAsync(project.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex)
        {
            blockers.Add(ex.Message);
            return new ReleaseContext { Blockers = blockers };
        }

        var repository = await ResolveGitHubRepositoryAsync(project, cancellationToken).ConfigureAwait(false);
        if (repository is null)
        {
            blockers.Add("This project has no GitHub remote. Publish it to GitHub from the GitHub tab first.");
        }

        if (!_github.IsSignedIn)
        {
            blockers.Add("You're not signed in to GitHub. Sign in to publish releases.");
        }

        var defaultBranch = await TryAsync(() => _git.GetDefaultBranchAsync(project.Path, cancellationToken)).ConfigureAwait(false);
        var context = new ReleaseContext
        {
            CurrentBranch = status.Branch,
            DefaultBranch = defaultBranch,
            HeadSha = status.HeadSha,
            HasUncommittedChanges = !status.IsClean,
            UnpushedCommits = status.Ahead,
        };

        if (status.IsUnborn)
        {
            blockers.Add("This repository has no commits yet. Commit your work before creating a release.");
            return context with { Blockers = blockers, Warnings = warnings };
        }

        if (status.IsDetached)
        {
            blockers.Add("HEAD is detached. Check out a branch before creating a release.");
        }

        AddWorkingStateWarnings(status, defaultBranch, warnings);

        var tags = await TryAsync(() => _git.GetTagsAsync(project.Path, cancellationToken)).ConfigureAwait(false) ?? [];
        var latest = tags
            .Select(t => SemVersion.TryParse(t.Name, out var v) ? new VersionedTag(t.Name, v) : null)
            .OfType<VersionedTag>()
            .OrderByDescending(t => t.Version)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        IReadOnlyList<GitCommit> commits;
        try
        {
            commits = await _git.GetLogAsync(project.Path, new GitLogQuery
            {
                Revision = latest is null ? null : $"{latest.Name}..HEAD",
                Take = MaxCommits,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex)
        {
            commits = [];
            warnings.Add($"The commits since the last release could not be read: {ex.Message}");
        }

        if (latest is not null && commits.Count == 0)
        {
            warnings.Add($"Nothing was committed since {latest.Name}: the new release would contain the same code.");
        }

        if (commits.Count >= MaxCommits)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Only the latest {MaxCommits:N0} commits are included in the draft notes."));
        }

        var suggestions = SuggestVersions(latest, commits);
        var tagPrefix = latest is null ? "v" : TagPrefixOf(latest.Name);
        var notes = ReleaseNotesBuilder.Build(commits, repository, latest?.Name, tagPrefix + suggestions[0].Version);

        IReadOnlyList<string> assets;
        try
        {
            assets = ReleaseAssetFinder.Find(project.Path, _clock.Now, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Release asset search failed in {Path}", project.Path);
            assets = [];
        }

        return context with
        {
            LatestTag = latest?.Name,
            LatestVersion = latest?.Version.ToString(),
            TagPrefix = tagPrefix,
            Suggestions = suggestions,
            CommitsSinceLatest = commits,
            DraftNotes = notes,
            Blockers = blockers,
            Warnings = warnings,
            SuggestedAssets = assets,
        };
    }

    public async Task<ReleaseResult> ExecuteAsync(Project project, ReleasePlan plan, IProgress<ReleaseStepUpdate>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);

        // Form fields often carry stray spaces; git and GitHub must receive the exact tag name.
        plan = plan with
        {
            TagName = plan.TagName?.Trim() ?? string.Empty,
            Version = plan.Version?.Trim() ?? string.Empty,
            Title = plan.Title?.Trim() ?? string.Empty,
            Target = string.IsNullOrWhiteSpace(plan.Target) ? null : plan.Target.Trim(),
            Assets = plan.Assets ?? [],
        };

        var run = new ReleaseRun(project, plan, new StepTimeline(progress, _logger));
        var validate = run.Timeline.Add(ReleaseStepKind.Validate, "Check the release");
        var createTag = run.Timeline.Add(ReleaseStepKind.CreateTag, $"Create tag {plan.TagName}");
        var pushTag = run.Timeline.Add(ReleaseStepKind.PushTag, $"Push tag {plan.TagName}");
        var createRelease = run.Timeline.Add(ReleaseStepKind.CreateRelease, "Create the release on GitHub");
        var uploads = plan.Assets.Select(a => (Path: a, Step: run.Timeline.Add(ReleaseStepKind.UploadAsset, $"Upload {Path.GetFileName(a)}"))).ToList();
        var publish = run.Timeline.Add(ReleaseStepKind.Publish, plan.Draft ? "Keep as draft" : "Publish the release");

        var succeeded =
            await RunStepAsync(run, validate, () => ValidateAsync(run, cancellationToken), cancellationToken).ConfigureAwait(false)
            && await RunStepAsync(run, createTag, () => CreateTagAsync(run, cancellationToken), cancellationToken).ConfigureAwait(false)
            && await RunStepAsync(run, pushTag, () => PushTagAsync(run, cancellationToken), cancellationToken).ConfigureAwait(false)
            && await RunStepAsync(run, createRelease, () => CreateReleaseAsync(run, cancellationToken), cancellationToken).ConfigureAwait(false);

        foreach (var (assetPath, step) in uploads)
        {
            succeeded = succeeded && await RunStepAsync(run, step, () => UploadAssetAsync(run, step, assetPath, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        succeeded = succeeded && await RunStepAsync(run, publish, () => PublishAsync(run, cancellationToken), cancellationToken).ConfigureAwait(false);

        return succeeded
            ? await CompleteAsync(run).ConfigureAwait(false)
            : await FailAsync(run).ConfigureAwait(false);
    }

    internal static IReadOnlyList<VersionSuggestion> SuggestVersions(VersionedTag? latest, IReadOnlyList<GitCommit> commits)
    {
        if (latest is null)
        {
            return
            [
                new VersionSuggestion("Initial development", "0.1.0",
                    "First release of a project that is still taking shape: 0.x tells users the API may still change."),
                new VersionSuggestion("First stable release", "1.0.0",
                    "Choose this if the project is ready for others to depend on."),
            ];
        }

        var version = latest.Version;
        if (version.IsPrerelease)
        {
            return
            [
                new VersionSuggestion("Stable release", version.BumpPatch().ToString(),
                    $"Recommended: publish the final version of {latest.Name}."),
                new VersionSuggestion("Next pre-release", version.BumpPrerelease().ToString(),
                    "Choose this to ship another preview before the stable release."),
            ];
        }

        var changes = commits.Where(c => !ConventionalCommit.IsMerge(c)).Select(ConventionalCommit.Parse).ToList();
        var breaking = changes.Count(c => c.IsBreaking);
        var features = changes.Count(c => !c.IsBreaking && c.Category == ChangeCategory.Feature);

        var major = new VersionSuggestion("Major", version.BumpMajor().ToString(), breaking > 0
            ? $"Recommended: {Plural(breaking, "breaking change")} since {latest.Name}."
            : "Choose this if the release breaks compatibility (removed or changed behavior).");
        var minor = new VersionSuggestion("Minor", version.BumpMinor().ToString(), breaking == 0 && features > 0
            ? $"Recommended: {Plural(features, "new feature")} since {latest.Name}."
            : "Choose this if the release adds features without breaking anything.");
        var patch = new VersionSuggestion("Patch", version.BumpPatch().ToString(), breaking > 0 || features > 0
            ? "Choose this if the release only fixes bugs."
            : commits.Count == 0 ? $"No changes since {latest.Name}." : $"Recommended: only fixes and maintenance since {latest.Name}.");

        return breaking > 0 ? [major, minor, patch]
            : features > 0 ? [minor, patch, major]
            : [patch, minor, major];
    }

    private async Task<StepOutcome> ValidateAsync(ReleaseRun run, CancellationToken cancellationToken)
    {
        var plan = run.Plan;
        if (string.IsNullOrWhiteSpace(plan.Title))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Give the release a title.");
        }

        if (!SemVersion.TryParse(plan.TagName, out var tagVersion))
        {
            throw new ForgeException(ErrorKind.InvalidInput,
                $"\"{plan.TagName}\" is not a valid version tag.",
                "Use the vMAJOR.MINOR.PATCH format, for example v1.4.0.");
        }

        if (!SemVersion.TryParse(plan.Version, out var version))
        {
            throw new ForgeException(ErrorKind.InvalidInput,
                $"\"{plan.Version}\" is not a valid version.",
                "Use the MAJOR.MINOR.PATCH format, for example 1.4.0.");
        }

        if (!version.Equals(tagVersion))
        {
            throw new ForgeException(ErrorKind.InvalidInput,
                $"The tag {plan.TagName} does not match version {plan.Version}.",
                "Use the same version number in both fields.");
        }

        if (!Directory.Exists(run.Project.Path))
        {
            throw new ForgeException(ErrorKind.PathNotFound, "The project folder no longer exists.", "Relocate the project, then try again.");
        }

        ValidateAssets(plan.Assets);

        run.Repository = await ResolveGitHubRepositoryAsync(run.Project, cancellationToken).ConfigureAwait(false)
            ?? throw new ForgeException(ErrorKind.NotFound, "This project has no GitHub remote.", "Publish the project to GitHub from the GitHub tab first.");
        if (!_github.IsSignedIn)
        {
            throw new ForgeException(ErrorKind.AuthenticationRequired, "You're not signed in to GitHub.", "Sign in from the GitHub tab, then retry.");
        }

        run.TargetSha = await ResolveTargetAsync(run, cancellationToken).ConfigureAwait(false);
        run.RemoteName = await ResolveRemoteNameAsync(run, cancellationToken).ConfigureAwait(false);

        var releases = await _github.GetReleasesAsync(run.Repository, 100, cancellationToken).ConfigureAwait(false);
        var existing = releases.FirstOrDefault(r => string.Equals(r.TagName, plan.TagName, StringComparison.Ordinal));
        if (existing is { IsDraft: false })
        {
            throw new ForgeException(ErrorKind.AlreadyExists,
                $"Release {plan.TagName} is already published on GitHub.",
                "Choose a new version number.");
        }

        var remoteTags = await _github.GetTagsAsync(run.Repository, cancellationToken).ConfigureAwait(false);
        var remoteTag = remoteTags.FirstOrDefault(t => string.Equals(t.Name, plan.TagName, StringComparison.Ordinal));
        if (remoteTag is not null && plan.CreateAndPushTag && !ShaMatches(remoteTag.Sha, run.TargetSha))
        {
            throw new ForgeException(ErrorKind.AlreadyExists,
                $"The tag {plan.TagName} already exists on GitHub and points to another commit.",
                "Choose a new version number, or delete the tag on GitHub first.");
        }

        // A tag at the right commit and a draft release are what a previous, interrupted attempt leaves behind.
        run.TagOnGitHub = remoteTag is not null;
        run.ExistingDraft = existing;
        return existing is not null
            ? StepOutcome.Done("Resuming the draft left by a previous attempt")
            : StepOutcome.Done($"{plan.TagName} → {Short(run.TargetSha)}");
    }

    private static void ValidateAssets(IReadOnlyList<string> assets)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            var name = Path.GetFileName(asset);
            var file = new FileInfo(asset);
            if (!Path.IsPathFullyQualified(asset) || !file.Exists)
            {
                throw new ForgeException(ErrorKind.PathNotFound, $"The asset {name} was not found.", "Build it again, or remove it from the release.", asset);
            }

            if (file.Length > MaxAssetBytes)
            {
                throw new ForgeException(ErrorKind.FileTooLarge, $"{name} is too large for GitHub ({PathUtil.FormatBytes(file.Length)}).",
                    "Release assets must be smaller than 2 GB.");
            }

            if (!names.Add(name))
            {
                throw new ForgeException(ErrorKind.InvalidInput, $"Two assets are named {name}.", "Rename one of them: asset names must be unique.");
            }
        }
    }

    private async Task<StepOutcome> CreateTagAsync(ReleaseRun run, CancellationToken cancellationToken)
    {
        var plan = run.Plan;
        if (!plan.CreateAndPushTag)
        {
            return StepOutcome.Skip("GitHub creates the tag when the release is published");
        }

        var localTags = await _git.GetTagsAsync(run.Project.Path, cancellationToken).ConfigureAwait(false);
        var existing = localTags.FirstOrDefault(t => string.Equals(t.Name, plan.TagName, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (ShaMatches(existing.TargetSha, run.TargetSha))
            {
                return StepOutcome.Skip($"Tag {plan.TagName} already points to {Short(run.TargetSha)}");
            }

            throw new ForgeException(ErrorKind.AlreadyExists,
                $"A local tag {plan.TagName} already exists and points to another commit.",
                "Delete the local tag from the Git tab, or choose a new version number.");
        }

        await _git.CreateTagAsync(run.Project.Path, plan.TagName, plan.Title, plan.Target, cancellationToken).ConfigureAwait(false);
        run.TagCreated = true;
        return StepOutcome.Done($"Annotated tag on {Short(run.TargetSha)}");
    }

    private async Task<StepOutcome> PushTagAsync(ReleaseRun run, CancellationToken cancellationToken)
    {
        if (!run.Plan.CreateAndPushTag)
        {
            return StepOutcome.Skip("GitHub creates the tag when the release is published");
        }

        if (run.TagOnGitHub)
        {
            return StepOutcome.Skip("The tag is already on GitHub");
        }

        await _git.PushTagAsync(run.Project.Path, run.Plan.TagName, run.RemoteName, cancellationToken).ConfigureAwait(false);
        run.TagOnGitHub = true;
        return StepOutcome.Done($"Pushed to {run.RemoteName ?? "origin"}");
    }

    private async Task<StepOutcome> CreateReleaseAsync(ReleaseRun run, CancellationToken cancellationToken)
    {
        if (run.ExistingDraft is { } draft)
        {
            run.Release = draft;
            return StepOutcome.Skip("Reusing the draft release from the previous attempt");
        }

        var plan = run.Plan;

        // With assets, the release stays a draft until every file is uploaded: a half-uploaded
        // release must never be public.
        var asDraft = plan.Draft || plan.Assets.Count > 0;
        run.Release = await _github.CreateReleaseAsync(run.Repository!, new NewRelease
        {
            TagName = plan.TagName,
            Name = plan.Title,
            Body = plan.Notes ?? string.Empty,
            TargetCommitish = plan.CreateAndPushTag ? null : run.TargetSha,
            Draft = asDraft,
            Prerelease = plan.Prerelease,
            MakeLatest = !plan.Prerelease,
        }, cancellationToken).ConfigureAwait(false);
        return StepOutcome.Done(asDraft ? "Created as a draft" : "Created");
    }

    private async Task<StepOutcome> UploadAssetAsync(ReleaseRun run, int step, string assetPath, CancellationToken cancellationToken)
    {
        var release = run.Release!;
        var name = Path.GetFileName(assetPath);
        var size = new FileInfo(assetPath).Length;
        var existing = release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.Size == size)
            {
                return StepOutcome.Skip("Already uploaded");
            }

            throw new ForgeException(ErrorKind.AlreadyExists,
                $"The draft release already has a different file named {name}.",
                "Delete that asset from the draft on GitHub, then retry.");
        }

        var reporter = new UploadProgress(run.Timeline, step);
        await _github.UploadReleaseAssetAsync(run.Repository!, release.Id, assetPath, reporter, cancellationToken).ConfigureAwait(false);
        return StepOutcome.Done(PathUtil.FormatBytes(size));
    }

    private async Task<StepOutcome> PublishAsync(ReleaseRun run, CancellationToken cancellationToken)
    {
        var release = run.Release!;
        if (run.Plan.Draft)
        {
            return StepOutcome.Skip("Kept as a draft: publish it on GitHub when you're ready");
        }

        if (!release.IsDraft)
        {
            return StepOutcome.Done("Published");
        }

        run.Release = await _github.PublishReleaseAsync(run.Repository!, release.Id, cancellationToken).ConfigureAwait(false);
        return StepOutcome.Done("Published");
    }

    private async Task<bool> RunStepAsync(ReleaseRun run, int step, Func<Task<StepOutcome>> action, CancellationToken cancellationToken)
    {
        run.Timeline.Set(step, ReleaseStepState.Running);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await action().ConfigureAwait(false);
            run.Timeline.Set(step, outcome.Skipped ? ReleaseStepState.Skipped : ReleaseStepState.Succeeded, outcome.Detail, outcome.Skipped ? null : 1.0);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Cancelled = true;
            run.Error = "The release was cancelled.";
            run.Timeline.Set(step, ReleaseStepState.Failed, "Cancelled");
            return false;
        }
        catch (Exception ex)
        {
            // Step failures are expected (network, permissions, rejected pushes): report them, never throw.
            if (ex is not ForgeException)
            {
                _logger.LogError(ex, "Release step {Step} failed unexpectedly", run.Timeline[step].Step);
            }

            var error = ErrorInfo.From(ex);
            run.Error = error.Hint is null ? error.Message : $"{error.Message} {error.Hint}";
            run.Timeline.Set(step, ReleaseStepState.Failed, run.Error);
            return false;
        }
    }

    private async Task<ReleaseResult> CompleteAsync(ReleaseRun run)
    {
        var release = run.Release!;
        await _activity.RecordAsync(new ActivityEntry
        {
            ProjectId = run.Project.Id,
            Kind = ActivityKind.ReleasePublished,
            Outcome = ActivityOutcome.Success,
            Title = release.IsDraft ? $"Created draft release {run.Plan.TagName}" : $"Published release {run.Plan.TagName}",
            Detail = run.Plan.Title,
            RefKind = UrlRefKind,
            RefValue = release.HtmlUrl,
        }, CancellationToken.None).ConfigureAwait(false);
        return new ReleaseResult(true, release, run.Timeline.Snapshot(), null);
    }

    private async Task<ReleaseResult> FailAsync(ReleaseRun run)
    {
        var error = run.Error ?? "The release could not be completed.";
        var tag = run.Plan.TagName;
        if (run.Release is { IsDraft: true })
        {
            error += $" The draft release {tag} was kept on GitHub: run the release again to resume it, or delete the draft there.";
        }
        else if (run.TagCreated && run.Release is null)
        {
            error += $" The tag {tag} was created locally and will be reused when you retry.";
        }

        await _activity.RecordAsync(new ActivityEntry
        {
            ProjectId = run.Project.Id,
            Kind = ActivityKind.ReleaseFailed,
            Outcome = run.Cancelled ? ActivityOutcome.Warning : ActivityOutcome.Failure,
            Title = run.Cancelled ? $"Release {tag} cancelled" : $"Release {tag} failed",
            Detail = error,
            RefKind = run.Release is null ? null : UrlRefKind,
            RefValue = run.Release?.HtmlUrl,
        }, CancellationToken.None).ConfigureAwait(false);
        return new ReleaseResult(false, run.Release, run.Timeline.Snapshot(), error);
    }

    private async Task<string> ResolveTargetAsync(ReleaseRun run, CancellationToken cancellationToken)
    {
        var revision = run.Plan.Target ?? "HEAD";
        var commits = await _git.GetLogAsync(run.Project.Path, new GitLogQuery { Revision = revision, Take = 1 }, cancellationToken).ConfigureAwait(false);
        return commits.Count > 0
            ? commits[0].Sha
            : throw new ForgeException(ErrorKind.NotFound, $"The commit to release ({revision}) was not found.", "Commit your work, or pick another target.");
    }

    private async Task<string?> ResolveRemoteNameAsync(ReleaseRun run, CancellationToken cancellationToken)
    {
        var remotes = await _git.GetRemotesAsync(run.Project.Path, cancellationToken).ConfigureAwait(false);
        return remotes.FirstOrDefault(r => SameRepository(GitHubRemoteParser.Parse(r.PushUrl ?? r.FetchUrl), run.Repository))?.Name;
    }

    /// <summary>The project's linked repository, or the one its remotes point to ("origin" first).</summary>
    private async Task<GitHubRepoRef?> ResolveGitHubRepositoryAsync(Project project, CancellationToken cancellationToken)
    {
        if (project.GitHub is not null)
        {
            return project.GitHub;
        }

        var remotes = await TryAsync(() => _git.GetRemotesAsync(project.Path, cancellationToken)).ConfigureAwait(false) ?? [];
        return remotes
            .OrderBy(r => r.Name == "origin" ? 0 : 1)
            .Select(r => GitHubRemoteParser.Parse(r.FetchUrl) ?? GitHubRemoteParser.Parse(r.PushUrl))
            .FirstOrDefault(r => r is not null);
    }

    private async Task<T?> TryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ForgeException ex)
        {
            _logger.LogDebug(ex, "Optional release information could not be read");
            return default;
        }
    }

    private static void AddWorkingStateWarnings(GitStatus status, string? defaultBranch, List<string> warnings)
    {
        if (!status.IsClean)
        {
            var count = status.Entries.Count;
            warnings.Add(count == 1
                ? "1 uncommitted change won't be part of this release. Commit it first if it belongs in it."
                : $"{Plural(count, "uncommitted change")} won't be part of this release. Commit them first if they belong in it.");
        }

        if (status.IsDetached || status.Branch is null)
        {
            return;
        }

        if (status.Upstream is null)
        {
            warnings.Add($"The branch {status.Branch} is not on GitHub yet. Push it so the release points to code others can see.");
        }
        else if (status.Ahead > 0)
        {
            warnings.Add($"{Plural(status.Ahead, "commit")} on {status.Branch} {(status.Ahead == 1 ? "is" : "are")} not pushed. Push first so GitHub has the code you release.");
        }

        if (status.Behind > 0)
        {
            warnings.Add($"{status.Branch} is {Plural(status.Behind, "commit")} behind {status.Upstream}. Pull first so the release includes the latest changes.");
        }

        if (defaultBranch is not null && !string.Equals(defaultBranch, status.Branch, StringComparison.Ordinal))
        {
            warnings.Add($"You are releasing from {status.Branch}, not from the default branch {defaultBranch}.");
        }
    }

    private static bool SameRepository(GitHubRepoRef? a, GitHubRepoRef? b) =>
        a is not null && b is not null
        && string.Equals(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Full or abbreviated SHAs of the same commit.</summary>
    private static bool ShaMatches(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && Math.Min(a.Length, b.Length) >= 7
        && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    private static string TagPrefixOf(string tagName) => char.IsAsciiLetter(tagName[0]) ? tagName[..1] : string.Empty;

    private static string Short(string? sha) => sha is { Length: > 7 } ? sha[..7] : sha ?? string.Empty;

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    internal sealed record VersionedTag(string Name, SemVersion Version);

    private sealed record StepOutcome(bool Skipped, string? Detail)
    {
        public static StepOutcome Done(string? detail = null) => new(false, detail);

        public static StepOutcome Skip(string detail) => new(true, detail);
    }

    /// <summary>Mutable state of one <see cref="ExecuteAsync"/> run.</summary>
    private sealed class ReleaseRun(Project project, ReleasePlan plan, StepTimeline timeline)
    {
        public Project Project { get; } = project;
        public ReleasePlan Plan { get; } = plan;
        public StepTimeline Timeline { get; } = timeline;
        public GitHubRepoRef? Repository { get; set; }
        public string? RemoteName { get; set; }
        public string? TargetSha { get; set; }
        public bool TagOnGitHub { get; set; }
        public bool TagCreated { get; set; }
        public GitHubRelease? ExistingDraft { get; set; }
        public GitHubRelease? Release { get; set; }
        public string? Error { get; set; }
        public bool Cancelled { get; set; }
    }

    /// <summary>Current state of every step, reported to the caller on each transition.</summary>
    private sealed class StepTimeline(IProgress<ReleaseStepUpdate>? progress, ILogger logger)
    {
        private readonly List<ReleaseStepUpdate> _steps = [];

        // Upload progress may be reported from the HTTP stack's threads.
        private readonly Lock _gate = new();

        public ReleaseStepUpdate this[int index]
        {
            get
            {
                lock (_gate)
                {
                    return _steps[index];
                }
            }
        }

        public int Add(ReleaseStepKind kind, string title)
        {
            var update = new ReleaseStepUpdate(kind, ReleaseStepState.Pending, title);
            int index;
            lock (_gate)
            {
                _steps.Add(update);
                index = _steps.Count - 1;
            }

            Report(update);
            return index;
        }

        public void Set(int index, ReleaseStepState state, string? detail = null, double? fraction = null)
        {
            ReleaseStepUpdate update;
            lock (_gate)
            {
                update = _steps[index] with { State = state, Detail = detail, Progress = fraction };
                _steps[index] = update;
            }

            Report(update);
        }

        public IReadOnlyList<ReleaseStepUpdate> Snapshot()
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }

        private void Report(ReleaseStepUpdate update)
        {
            try
            {
                progress?.Report(update);
            }
            catch (Exception ex)
            {
                // A faulty progress handler (UI) must not abort a half-done release.
                logger.LogWarning(ex, "Release progress handler failed");
            }
        }
    }

    /// <summary>Turns byte-level upload progress into step updates, at most one per percent.</summary>
    private sealed class UploadProgress(StepTimeline timeline, int step) : IProgress<TransferProgress>
    {
        private int _lastPercent = -1;

        public void Report(TransferProgress value)
        {
            var percent = (int)Math.Floor(Math.Clamp(value.Fraction, 0, 1) * 100);
            if (Interlocked.Exchange(ref _lastPercent, percent) == percent)
            {
                return;
            }

            timeline.Set(step, ReleaseStepState.Running,
                $"{PathUtil.FormatBytes(value.BytesTransferred)} of {PathUtil.FormatBytes(value.TotalBytes)}", percent / 100.0);
        }
    }
}
