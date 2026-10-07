using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Releases;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Releases;

/// <summary>The steps of the release wizard; <see cref="Publish"/> is the execution timeline and its result.</summary>
public enum ReleaseWizardStep
{
    Version,
    Notes,
    Assets,
    Review,
    Publish,
}

/// <summary>
/// "New release" wizard (Version · Notes · Assets · Review), shown full height inside the Releases
/// tab. It prepares the form from <see cref="IReleaseService.PrepareAsync"/>, validates each step,
/// keeps the notes in sync with the version until the user edits them, then runs
/// <see cref="IReleaseService.ExecuteAsync"/> showing every step as a timeline, and offers to retry
/// the same plan when a step fails.
/// </summary>
public sealed partial class ReleaseWizardViewModel : ViewModelBase, IDisposable
{
    private readonly ProjectContext _context;
    private readonly WorkspaceServices _services;
    private readonly IReleaseService _releases;
    private readonly IGitHubService _gitHub;
    private readonly HashSet<string> _releasedTags;
    private readonly string? _initialVersion;
    private readonly Action<ReleaseWizardViewModel> _closed;
    private ReleaseContext? _release;
    private SemVersion? _version;
    private ReleasePlan? _plan;
    private CancellationTokenSource? _execution;
    private int _executionId;
    private bool _applyingNotes;
    private bool _applyingTitle;
    private bool _applyingPrerelease;
    private bool _titleEdited;
    private bool _prereleaseEdited;
    private bool _disposed;

    public ReleaseWizardViewModel(ProjectContext context, WorkspaceServices services, IReleaseService releases, IGitHubService gitHub,
        IEnumerable<string> releasedTags, string? initialVersion, Action<ReleaseWizardViewModel> closed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(gitHub);
        ArgumentNullException.ThrowIfNull(releasedTags);
        ArgumentNullException.ThrowIfNull(closed);
        _context = context;
        _services = services;
        _releases = releases;
        _gitHub = gitHub;
        _releasedTags = new HashSet<string>(releasedTags, StringComparer.OrdinalIgnoreCase);
        _initialVersion = initialVersion;
        _closed = closed;
        Steps =
        [
            new WizardStepViewModel(ReleaseWizardStep.Version, 1, "Version"),
            new WizardStepViewModel(ReleaseWizardStep.Notes, 2, "Notes"),
            new WizardStepViewModel(ReleaseWizardStep.Assets, 3, "Assets"),
            new WizardStepViewModel(ReleaseWizardStep.Review, 4, "Review"),
        ];
        UpdateSteps();
    }

    public string ProjectName => _context.Project.Name;

    public ObservableCollection<WizardStepViewModel> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVersionStep), nameof(IsNotesStep), nameof(IsAssetsStep), nameof(IsReviewStep), nameof(IsPublishStep),
        nameof(IsFirstStep), nameof(NextText), nameof(IsFormStep))]
    public partial ReleaseWizardStep CurrentStep { get; private set; }

    public bool IsVersionStep => CurrentStep == ReleaseWizardStep.Version;

    public bool IsNotesStep => CurrentStep == ReleaseWizardStep.Notes;

    public bool IsAssetsStep => CurrentStep == ReleaseWizardStep.Assets;

    public bool IsReviewStep => CurrentStep == ReleaseWizardStep.Review;

    public bool IsPublishStep => CurrentStep == ReleaseWizardStep.Publish;

    /// <summary>One of the four form steps (the footer shows Back / Next).</summary>
    public bool IsFormStep => CurrentStep != ReleaseWizardStep.Publish;

    public bool IsFirstStep => CurrentStep == ReleaseWizardStep.Version;

    public string NextText => CurrentStep == ReleaseWizardStep.Review ? "Publish release" : "Next";

    [ObservableProperty]
    public partial bool IsPreparing { get; private set; }

    [ObservableProperty]
    public partial bool IsPrepared { get; private set; }

    // ----- Version step -------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockers), nameof(BlockersText))]
    public partial IReadOnlyList<string> Blockers { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings), nameof(WarningsText))]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    public bool HasBlockers => Blockers.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;

    public string BlockersText => string.Join(Environment.NewLine, Blockers);

    public string WarningsText => string.Join(Environment.NewLine, Warnings);

    /// <summary>"Latest release v1.2.0 · 12 commits since" or "First release · 34 commits".</summary>
    [ObservableProperty]
    public partial string? LatestText { get; private set; }

    [ObservableProperty]
    public partial string TagPrefix { get; private set; } = "v";

    public ObservableCollection<VersionOptionViewModel> VersionOptions { get; } = [];

    [ObservableProperty]
    public partial VersionOptionViewModel? SelectedOption { get; private set; }

    [ObservableProperty]
    public partial bool UseCustomVersion { get; set; }

    [ObservableProperty]
    public partial string CustomVersion { get; set; } = string.Empty;

    /// <summary>The chosen version ("1.2.0"), or null while invalid.</summary>
    public string? Version => _version?.ToString();

    /// <summary>The tag the release creates ("v1.2.0").</summary>
    public string? TagName => _version is null ? null : TagPrefix + _version;

    [ObservableProperty]
    public partial string? VersionError { get; private set; }

    [ObservableProperty]
    public partial string? VersionWarning { get; private set; }

    [ObservableProperty]
    public partial string ReleaseTitle { get; set; } = string.Empty;

    public IReadOnlyList<ReleaseTargetOption> TargetOptions { get; private set; } = [];

    [ObservableProperty]
    public partial ReleaseTargetOption? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial bool IsPrerelease { get; set; }

    [ObservableProperty]
    public partial bool IsDraft { get; set; }

    // ----- Notes step ---------------------------------------------------------------------

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    /// <summary>The user changed the notes: they no longer follow the version.</summary>
    [ObservableProperty]
    public partial bool NotesEdited { get; private set; }

    [ObservableProperty]
    public partial bool ShowPreview { get; set; }

    [ObservableProperty]
    public partial bool IsGeneratingNotes { get; private set; }

    // ----- Assets step --------------------------------------------------------------------

    public ObservableCollection<ReleaseAssetFileViewModel> Assets { get; } = [];

    public bool HasAssets => Assets.Count > 0;

    /// <summary>"2 files · 14.2 MB" for the ticked assets.</summary>
    public string AssetsSummary
    {
        get
        {
            var selected = Assets.Where(a => a.IsSelected).ToList();
            return selected.Count == 0 ? "No files will be attached" : $"{Format.Count(selected.Count, "file")} · {Format.Bytes(selected.Sum(a => a.Size))}";
        }
    }

    [ObservableProperty]
    public partial string? AssetsError { get; private set; }

    // ----- Review step --------------------------------------------------------------------

    public string ReviewTarget => SelectedTarget is { } target ? $"{target.Title} · {target.Description}" : "HEAD";

    public string ReviewKind => IsDraft
        ? (IsPrerelease ? "Saved as a draft pre-release (not visible to others yet)" : "Saved as a draft (not visible to others yet)")
        : IsPrerelease ? "Published as a pre-release" : "Published as the latest release";

    public IReadOnlyList<ReleaseAssetFileViewModel> SelectedAssets => Assets.Where(a => a.IsSelected).ToList();

    public string ReviewAssets => SelectedAssets.Count == 0 ? "None" : string.Join(", ", SelectedAssets.Select(a => a.Name));

    // ----- Publish step -------------------------------------------------------------------

    public ObservableCollection<ReleaseTimelineStepViewModel> Timeline { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelText))]
    public partial bool IsExecuting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSucceeded), nameof(IsFailed))]
    public partial ReleaseResult? Result { get; private set; }

    public bool IsSucceeded => Result is { Succeeded: true };

    public bool IsFailed => Result is { Succeeded: false };

    /// <summary>"ForgeDesk 1.2.0 is live".</summary>
    public string SuccessTitle => Result?.Release is { IsDraft: true } ? $"{_plan?.Title} is saved as a draft" : $"{_plan?.Title} is live";

    public string SuccessDescription => Result?.Release is { } release
        ? $"Tag {release.TagName}" + (release.Assets.Count > 0 ? $" · {Format.Count(release.Assets.Count, "asset")}" : string.Empty)
          + (release.IsDraft ? " · publish it from GitHub when you're ready." : " · published on GitHub.")
        : string.Empty;

    public string? FailureMessage => Result is { Succeeded: false } result ? result.Error ?? "The release could not be completed." : null;

    public string CancelText => IsExecuting ? "Stop" : "Cancel";

    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsPreparing = true;
        try
        {
            await RunAsync(async () =>
            {
                var release = await _releases.PrepareAsync(_context.Project, _context.Lifetime).ConfigureAwait(true);
                Apply(release);
            }, "Preparing the release…", "Could not prepare the release").ConfigureAwait(true);
        }
        finally
        {
            IsPreparing = false;
        }
    }

    [RelayCommand]
    private Task RetryPrepareAsync() => InitializeAsync();

    [RelayCommand]
    private void SelectOption(VersionOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }

        UseCustomVersion = false;
        SelectedOption = option;
        foreach (var candidate in VersionOptions)
        {
            candidate.IsSelected = ReferenceEquals(candidate, option);
        }

        UpdateVersion();
    }

    [RelayCommand]
    private void ChooseCustomVersion()
    {
        UseCustomVersion = true;
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (!CanGoNext())
        {
            return;
        }

        if (CurrentStep == ReleaseWizardStep.Review)
        {
            await PublishAsync().ConfigureAwait(true);
            return;
        }

        GoTo(CurrentStep + 1);
    }

    [RelayCommand]
    private void Back()
    {
        if (CurrentStep is > ReleaseWizardStep.Version and < ReleaseWizardStep.Publish)
        {
            GoTo(CurrentStep - 1);
        }
    }

    /// <summary>Clicking a step of the header: any earlier step, or a later one when every step before it is valid.</summary>
    [RelayCommand]
    private void GoToStep(WizardStepViewModel? step)
    {
        if (step is null || IsPublishStep || !IsPrepared)
        {
            return;
        }

        for (var s = ReleaseWizardStep.Version; s < step.Step; s++)
        {
            if (ValidationError(s) is not null)
            {
                GoTo(s);
                return;
            }
        }

        GoTo(step.Step);
    }

    /// <summary>Replaces the notes with GitHub's generated ones (after confirmation when notes exist).</summary>
    [RelayCommand]
    private async Task GenerateNotesAsync()
    {
        if (IsGeneratingNotes || TagName is not { } tag || _context.Project.GitHub is not { } repo)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Notes) && !await _services.Dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = "Replace the release notes?",
            Message = "GitHub writes notes from the pull requests merged since the previous release. They replace the current notes.",
            ConfirmText = "Replace notes",
        }).ConfigureAwait(true))
        {
            return;
        }

        IsGeneratingNotes = true;
        try
        {
            await RunAsync(async () =>
            {
                var notes = await _gitHub.GenerateReleaseNotesAsync(repo, tag, _release?.LatestTag, SelectedTarget?.Value, _context.Lifetime).ConfigureAwait(true);
                SetNotes(notes, edited: true);
            }, errorTitle: "Could not generate the notes", errorMode: ErrorMode.Toast, notifications: _services.Notifications).ConfigureAwait(true);
        }
        finally
        {
            IsGeneratingNotes = false;
        }
    }

    /// <summary>Drops the user's edits and drafts the notes again from the commits.</summary>
    [RelayCommand]
    private void RebuildNotes()
    {
        if (_release is null)
        {
            return;
        }

        SetNotes(BuildNotes(), edited: false);
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        IReadOnlyList<string> files;
        try
        {
            files = await _services.Dialogs.PickFilesAsync("Add release assets", _context.Root, allowMultiple: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            _services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the file picker"));
            return;
        }

        foreach (var file in files)
        {
            var existing = Assets.FirstOrDefault(a => PathUtil.Comparer.Equals(a.Path, file));
            if (existing is not null)
            {
                existing.IsSelected = true;
                continue;
            }

            AddAsset(new ReleaseAssetFileViewModel(file, isSuggested: false));
        }

        AssetsChanged();
    }

    [RelayCommand]
    private void RemoveAsset(ReleaseAssetFileViewModel? asset)
    {
        if (asset is not null && Assets.Remove(asset))
        {
            asset.PropertyChanged -= OnAssetPropertyChanged;
            AssetsChanged();
        }
    }

    [RelayCommand]
    private Task PublishAsync()
    {
        if (IsExecuting || !IsPrepared)
        {
            return Task.CompletedTask;
        }

        for (var s = ReleaseWizardStep.Version; s <= ReleaseWizardStep.Review; s++)
        {
            if (ValidationError(s) is not null)
            {
                GoTo(s);
                return Task.CompletedTask;
            }
        }

        _plan = BuildPlan();
        return ExecuteAsync(_plan);
    }

    /// <summary>Runs the same plan again (the service resumes from what exists: tag, draft release).</summary>
    [RelayCommand]
    private Task RetryAsync() => _plan is null || IsExecuting ? Task.CompletedTask : ExecuteAsync(_plan);

    /// <summary>Back to the review after a failure, to change the plan before retrying.</summary>
    [RelayCommand]
    private void EditRelease()
    {
        if (!IsExecuting && IsFailed)
        {
            Result = null;
            Timeline.Clear();
            GoTo(ReleaseWizardStep.Review);
        }
    }

    /// <summary>Esc / Cancel: closes the wizard, or asks before stopping a release being published.</summary>
    [RelayCommand]
    private async Task CancelAsync()
    {
        if (IsExecuting)
        {
            if (await _services.Dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = "Stop publishing?",
                Message = "ForgeDesk stops after the current step. What is already done (the tag, a draft release on GitHub) stays, and is reused when you publish again.",
                ConfirmText = "Stop publishing",
                CancelText = "Keep going",
                IsDestructive = true,
            }).ConfigureAwait(true))
            {
                _execution?.Cancel();
            }

            return;
        }

        Close();
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsExecuting)
        {
            _closed(this);
        }
    }

    [RelayCommand]
    private void ViewOnGitHub() => OpenLink(Result?.Release?.HtmlUrl);

    /// <summary>A link clicked in the notes preview.</summary>
    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            _services.Shell.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
        }
    }

    [RelayCommand]
    private void CopyLink()
    {
        if (Result?.Release?.HtmlUrl is { } url)
        {
            try
            {
                _services.Shell.CopyToClipboard(url);
                _services.Notifications.Show("Link copied", url, NotificationSeverity.Info);
            }
            catch (Exception ex)
            {
                _services.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _execution?.Cancel();
        foreach (var asset in Assets)
        {
            asset.PropertyChanged -= OnAssetPropertyChanged;
        }
    }

    /// <summary>Why <paramref name="step"/> can't be left yet, or null when it is complete.</summary>
    internal string? ValidationError(ReleaseWizardStep step) => step switch
    {
        ReleaseWizardStep.Version when !IsPrepared => "The release is still being prepared.",
        ReleaseWizardStep.Version when HasBlockers => Blockers[0],
        ReleaseWizardStep.Version when VersionError is not null => VersionError,
        ReleaseWizardStep.Version when string.IsNullOrWhiteSpace(ReleaseTitle) => "Give the release a title.",
        ReleaseWizardStep.Assets => AssetsError,
        _ => null,
    };

    private bool CanGoNext() => IsFormStep && !IsExecuting && ValidationError(CurrentStep) is null;

    private void Apply(ReleaseContext release)
    {
        _release = release;
        TagPrefix = release.TagPrefix;
        Blockers = release.Blockers;
        Warnings = release.Warnings;
        var commits = Format.Count(release.CommitsSinceLatest.Count, "commit");
        LatestText = release.LatestTag is { } latest ? $"Latest release {latest} · {commits} since" : $"First release · {commits}";

        VersionOptions.Clear();
        for (var i = 0; i < release.Suggestions.Count; i++)
        {
            VersionOptions.Add(new VersionOptionViewModel(release.Suggestions[i], release.TagPrefix, isRecommended: i == 0));
        }

        var targets = new List<ReleaseTargetOption>();
        if (release.CurrentBranch is { } branch)
        {
            targets.Add(new ReleaseTargetOption($"Branch {branch}", branch, "its latest commit when the tag is created"));
        }

        if (release.HeadSha is { Length: > 0 } sha)
        {
            targets.Add(new ReleaseTargetOption("This commit", sha, $"HEAD {(sha.Length > 7 ? sha[..7] : sha)}"));
        }

        TargetOptions = targets;
        OnPropertyChanged(nameof(TargetOptions));
        SelectedTarget = targets.FirstOrDefault();

        foreach (var asset in Assets)
        {
            asset.PropertyChanged -= OnAssetPropertyChanged;
        }

        Assets.Clear();
        foreach (var path in release.SuggestedAssets)
        {
            AddAsset(new ReleaseAssetFileViewModel(path, isSuggested: true));
        }

        _titleEdited = false;
        _prereleaseEdited = false;
        _version = null;
        NotesEdited = false;
        IsPrepared = true;

        if (_initialVersion is { Length: > 0 } initial)
        {
            var wanted = SemVersion.TryParse(initial, out var parsed) ? parsed.ToString() : initial.Trim();
            if (VersionOptions.FirstOrDefault(o => o.Version == wanted) is { } match)
            {
                SelectOption(match);
            }
            else
            {
                CustomVersion = wanted;
                UseCustomVersion = true;
                UpdateVersion();
            }
        }
        else if (VersionOptions.Count > 0)
        {
            SelectOption(VersionOptions[0]);
        }
        else
        {
            UseCustomVersion = true;
            UpdateVersion();
        }

        // The prepared draft matches the recommended version; another version gets notes of its own.
        var recommended = !UseCustomVersion && ReferenceEquals(SelectedOption, VersionOptions.FirstOrDefault());
        SetNotes(recommended || _version is null ? release.DraftNotes : BuildNotes(), edited: false);
        AssetsChanged();
        GoTo(ReleaseWizardStep.Version);
    }

    private void UpdateVersion()
    {
        var text = UseCustomVersion ? CustomVersion.Trim() : SelectedOption?.Version;
        SemVersion? version = null;
        string? error = null;
        string? warning = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = UseCustomVersion ? "Enter a version, for example 1.4.0 or 2.0.0-beta.1." : "Choose a version.";
        }
        else if (!SemVersion.TryParse(text, out version))
        {
            error = $"\"{text}\" isn't a valid version. Use MAJOR.MINOR.PATCH, for example 1.4.0 or 2.0.0-beta.1.";
        }
        else if (_releasedTags.Contains(TagPrefix + version))
        {
            error = $"A release for {TagPrefix}{version} already exists on GitHub. Choose another version.";
            version = null;
        }
        else if (_release?.LatestVersion is { } latestText && SemVersion.TryParse(latestText, out var latest) && version <= latest)
        {
            warning = $"{version} isn't newer than the latest release ({latest}): people may not notice it as an update.";
        }

        var changed = !Equals(version, _version);
        _version = version;
        VersionError = error;
        VersionWarning = warning;
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(TagName));

        if (version is not null && changed)
        {
            if (!_titleEdited)
            {
                SetTitle($"{_context.Project.Name} {version}");
            }

            if (!_prereleaseEdited)
            {
                _applyingPrerelease = true;
                try
                {
                    IsPrerelease = version.IsPrerelease;
                }
                finally
                {
                    _applyingPrerelease = false;
                }
            }

            if (!NotesEdited && _release is not null && IsPrepared && !_applyingNotes)
            {
                SetNotes(BuildNotes(), edited: false);
            }
        }

        Revalidate();
    }

    private string BuildNotes() =>
        ReleaseNotesBuilder.Build(_release?.CommitsSinceLatest ?? [], _context.Project.GitHub, _release?.LatestTag, TagName);

    private void SetNotes(string notes, bool edited)
    {
        _applyingNotes = true;
        try
        {
            Notes = notes;
        }
        finally
        {
            _applyingNotes = false;
        }

        NotesEdited = edited;
    }

    private void SetTitle(string title)
    {
        _applyingTitle = true;
        try
        {
            ReleaseTitle = title;
        }
        finally
        {
            _applyingTitle = false;
        }
    }

    private ReleasePlan BuildPlan() => new()
    {
        Version = _version!.ToString(),
        TagName = TagName!,
        Title = ReleaseTitle.Trim(),
        Notes = Notes,
        Target = SelectedTarget?.Value,
        Draft = IsDraft,
        Prerelease = IsPrerelease,
        Assets = SelectedAssets.Select(a => a.Path).ToList(),
    };

    private async Task ExecuteAsync(ReleasePlan plan)
    {
        _execution?.Cancel();
        _execution?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_context.Lifetime);
        _execution = cts;
        var id = ++_executionId;
        Timeline.Clear();
        Result = null;
        IsExecuting = true;
        GoTo(ReleaseWizardStep.Publish);
        NotifyResult();

        ReleaseResult result;
        try
        {
            var progress = new UiProgress(_services.Dispatcher, update =>
            {
                if (id == _executionId && IsExecuting)
                {
                    ApplyUpdate(update);
                }
            });
            result = await _releases.ExecuteAsync(_context.Project, plan, progress, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var info = ErrorInfo.From(ex);
            result = new ReleaseResult(false, null, [], ex.IsCancellation() ? "Publishing was stopped." : info.Hint is null ? info.Message : $"{info.Message} {info.Hint}");
        }

        if (id != _executionId || _disposed)
        {
            return;
        }

        if (result.Steps.Count > 0)
        {
            ApplyFinal(result.Steps);
        }

        IsExecuting = false;
        Result = result;
        NotifyResult();
        if (result.Succeeded)
        {
            try
            {
                _services.Notifications.ShowSystemNotification(SuccessTitle, $"{_context.Project.Name} · {plan.TagName}", _context.ProjectId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Could not show the release notification: {ex.Message}");
            }

            Published?.Invoke(this, result.Release);
        }
    }

    /// <summary>Raised after a successful release (the list reloads).</summary>
    public event EventHandler<GitHubRelease?>? Published;

    /// <summary>
    /// Maps a step transition to its row. Steps are announced (Pending) in order before any starts, so
    /// a Pending update while nothing started yet is a new row; later updates go to the first unfinished
    /// row of the same step and title (uploads of files with the same name stay distinct).
    /// </summary>
    internal void ApplyUpdate(ReleaseStepUpdate update)
    {
        if (update.State == ReleaseStepState.Pending && Timeline.All(r => r.IsPending))
        {
            Timeline.Add(new ReleaseTimelineStepViewModel(update));
            return;
        }

        var row = Timeline.FirstOrDefault(r => r.Kind == update.Step && r.Title == update.Title && (r.IsPending || r.IsRunning))
            ?? Timeline.LastOrDefault(r => r.Kind == update.Step && r.Title == update.Title);
        if (row is null)
        {
            Timeline.Add(new ReleaseTimelineStepViewModel(update));
        }
        else
        {
            row.Apply(update);
        }
    }

    /// <summary>The result's snapshot is authoritative: align every row with it.</summary>
    private void ApplyFinal(IReadOnlyList<ReleaseStepUpdate> steps)
    {
        if (steps.Count == Timeline.Count && steps.Select(s => (s.Step, s.Title)).SequenceEqual(Timeline.Select(r => (r.Kind, r.Title))))
        {
            for (var i = 0; i < steps.Count; i++)
            {
                Timeline[i].Apply(steps[i]);
            }

            return;
        }

        Timeline.Clear();
        foreach (var step in steps)
        {
            Timeline.Add(new ReleaseTimelineStepViewModel(step));
        }
    }

    private void NotifyResult()
    {
        OnPropertyChanged(nameof(SuccessTitle));
        OnPropertyChanged(nameof(SuccessDescription));
        OnPropertyChanged(nameof(FailureMessage));
        Revalidate();
    }

    private void GoTo(ReleaseWizardStep step)
    {
        CurrentStep = step;
        UpdateSteps();
        if (step == ReleaseWizardStep.Review)
        {
            OnPropertyChanged(nameof(ReviewTarget));
            OnPropertyChanged(nameof(ReviewKind));
            OnPropertyChanged(nameof(SelectedAssets));
            OnPropertyChanged(nameof(ReviewAssets));
        }

        Revalidate();
    }

    private void UpdateSteps()
    {
        foreach (var item in Steps)
        {
            item.IsCurrent = item.Step == CurrentStep;
            item.IsCompleted = item.Step < CurrentStep;
        }
    }

    /// <summary>Tooltip of the Next button: why it is disabled, or what it does.</summary>
    public string NextHint => IsFormStep && ValidationError(CurrentStep) is { } error ? error
        : CurrentStep == ReleaseWizardStep.Review ? "Create and push the tag, then publish the release on GitHub"
        : "Continue";

    private void Revalidate()
    {
        NextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(NextHint));
    }

    private void AddAsset(ReleaseAssetFileViewModel asset)
    {
        asset.PropertyChanged += OnAssetPropertyChanged;
        Assets.Add(asset);
    }

    private void OnAssetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReleaseAssetFileViewModel.IsSelected))
        {
            AssetsChanged();
        }
    }

    private void AssetsChanged()
    {
        var selected = Assets.Where(a => a.IsSelected).ToList();
        var missing = selected.FirstOrDefault(a => !a.Exists);
        var duplicate = selected.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        var tooLarge = selected.FirstOrDefault(a => a.Size >= 2L * 1024 * 1024 * 1024);
        AssetsError = missing is not null ? $"{missing.Name} no longer exists. Build it again, or remove it."
            : duplicate is not null ? $"Two files are named {duplicate.Key}: GitHub needs unique asset names. Untick or remove one."
            : tooLarge is not null ? $"{tooLarge.Name} is larger than 2 GB, the limit of GitHub release assets."
            : null;
        OnPropertyChanged(nameof(HasAssets));
        OnPropertyChanged(nameof(AssetsSummary));
        OnPropertyChanged(nameof(SelectedAssets));
        OnPropertyChanged(nameof(ReviewAssets));
        Revalidate();
    }

    partial void OnUseCustomVersionChanged(bool value)
    {
        if (value)
        {
            foreach (var option in VersionOptions)
            {
                option.IsSelected = false;
            }

            if (CustomVersion.Length == 0 && SelectedOption is { } previous)
            {
                CustomVersion = previous.Version;
            }
        }

        if (IsPrepared)
        {
            UpdateVersion();
        }
    }

    partial void OnCustomVersionChanged(string value)
    {
        if (UseCustomVersion && IsPrepared)
        {
            UpdateVersion();
        }
    }

    partial void OnReleaseTitleChanged(string value)
    {
        if (!_applyingTitle)
        {
            _titleEdited = true;
        }

        Revalidate();
    }

    partial void OnIsPrereleaseChanged(bool value)
    {
        if (!_applyingPrerelease)
        {
            _prereleaseEdited = true;
        }
    }

    partial void OnNotesChanged(string value)
    {
        if (!_applyingNotes)
        {
            NotesEdited = true;
        }
    }

    /// <summary>Reports progress on the UI thread, in order.</summary>
    private sealed class UiProgress(IUiDispatcher dispatcher, Action<ReleaseStepUpdate> apply) : IProgress<ReleaseStepUpdate>
    {
        public void Report(ReleaseStepUpdate value) => dispatcher.Post(() => apply(value));
    }
}
