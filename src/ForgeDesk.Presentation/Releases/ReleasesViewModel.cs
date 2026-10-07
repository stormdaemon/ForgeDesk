using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Releases;
using ForgeDesk.Presentation.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Releases;

/// <summary>
/// The Releases tab: the GitHub releases of the linked repository (tag, title, Latest / Pre-release /
/// Draft, assets with download counts, expandable notes), local version tags that have no release yet,
/// and the "New release" wizard shown full height in place of the list.
/// </summary>
public sealed partial class ReleasesViewModel : GitHubLinkedSectionViewModel, INavigationTarget, IRefreshable
{
    /// <summary>How many releases are listed.</summary>
    public const int MaxReleases = 50;

    /// <summary>How many unreleased tags the hint names.</summary>
    public const int MaxUnreleasedTags = 5;

    private readonly IReleaseService _releaseService;
    private CancellationTokenSource? _load;
    private bool _hasLoaded;
    private bool _stale;
    private ReleasesNavigation? _pendingNavigation;

    public ReleasesViewModel(ProjectContext context, WorkspaceServices services, IGitHubService gitHub, IGitHubAccountService accounts, IReleaseService releases)
        : base(context, services, gitHub, accounts)
    {
        ArgumentNullException.ThrowIfNull(releases);
        _releaseService = releases;
        Context.RepositoryChanged += OnGitRepositoryChanged;
    }

    public override WorkspaceSection Section => WorkspaceSection.Releases;

    public ObservableCollection<ReleaseRowViewModel> Releases { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Version tags that exist locally but have no GitHub release ("v1.3.0").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnreleasedTags), nameof(UnreleasedTagsText))]
    public partial IReadOnlyList<string> UnreleasedTags { get; private set; } = [];

    public bool HasUnreleasedTags => UnreleasedTags.Count > 0;

    public string UnreleasedTagsText => UnreleasedTags.Count switch
    {
        0 => string.Empty,
        1 => $"{UnreleasedTags[0]} is tagged locally but has no GitHub release.",
        _ => $"{string.Join(", ", UnreleasedTags)} are tagged locally but have no GitHub release.",
    };

    /// <summary>The "New release" wizard, shown instead of the list while open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWizardOpen), nameof(IsListVisible))]
    public partial ReleaseWizardViewModel? Wizard { get; private set; }

    public bool IsWizardOpen => Wizard is not null;

    public bool IsListVisible => IsReady && Wizard is null;

    /// <summary>Deep link: a <see cref="ReleasesNavigation"/> (open the wizard, optionally for a tag).</summary>
    public async Task NavigateToAsync(object argument)
    {
        if (argument is not ReleasesNavigation navigation || IsDisposed)
        {
            return;
        }

        if (!IsReady || !IsActive)
        {
            _pendingNavigation = navigation;
            return;
        }

        if (navigation.StartNewRelease)
        {
            await StartWizardAsync(navigation.Version).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsReady || Repository is not { } repo)
        {
            return;
        }

        GitHub.InvalidateCache(repo);
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Retry of the error panel.</summary>
    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private Task NewReleaseAsync() => StartWizardAsync(null);

    /// <summary>Opens the wizard for a local tag that has no release yet.</summary>
    [RelayCommand]
    private Task ReleaseTagAsync(string? tag) => StartWizardAsync(tag);

    [RelayCommand]
    private void OpenRelease(ReleaseRowViewModel? release) => OpenUrl(release?.HtmlUrl);

    [RelayCommand]
    private void OpenAsset(ReleaseAssetViewModel? asset) => OpenUrl(asset?.DownloadUrl);

    /// <summary>A link clicked in rendered release notes.</summary>
    [RelayCommand]
    private void OpenLink(string? url) => OpenUrl(url);

    [RelayCommand]
    private void ToggleNotes(ReleaseRowViewModel? release)
    {
        if (release is not null)
        {
            release.IsExpanded = !release.IsExpanded;
        }
    }

    [RelayCommand]
    private void CopyLink(ReleaseRowViewModel? release)
    {
        if (release is null)
        {
            return;
        }

        try
        {
            Services.Shell.CopyToClipboard(release.HtmlUrl);
            Services.Notifications.Show("Link copied", release.HtmlUrl, NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    public async Task LoadAsync()
    {
        if (IsDisposed || !IsReady || Repository is not { } repo)
        {
            return;
        }

        _hasLoaded = true;
        _stale = false;
        _load?.Cancel();
        _load?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Context.Lifetime);
        _load = cts;
        var token = cts.Token;
        IsLoading = Releases.Count == 0;
        try
        {
            await RunAsync(async () =>
            {
                var releasesTask = GitHub.GetReleasesAsync(repo, MaxReleases, token);
                var tagsTask = LocalTagsAsync(token);
                var releases = await releasesTask.ConfigureAwait(true);
                var tags = await tagsTask.ConfigureAwait(true);
                token.ThrowIfCancellationRequested();

                var latest = releases.Where(r => !r.IsDraft && !r.IsPrerelease)
                    .OrderByDescending(r => r.PublishedAt ?? r.CreatedAt)
                    .FirstOrDefault();
                var expanded = Releases.Where(r => r.IsExpanded).Select(r => r.Id).ToHashSet();
                Releases.Clear();
                foreach (var release in releases)
                {
                    Releases.Add(new ReleaseRowViewModel(release, ReferenceEquals(release, latest)) { IsExpanded = expanded.Contains(release.Id) });
                }

                if (Releases.Count > 0 && expanded.Count == 0)
                {
                    // The newest release opens with its notes visible.
                    Releases[0].IsExpanded = Releases[0].HasNotes;
                }

                var released = releases.Select(r => r.TagName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                UnreleasedTags = tags
                    .Where(t => !released.Contains(t.Name) && SemVersion.TryParse(t.Name, out _))
                    .Select(t => (t.Name, Version: SemVersion.Parse(t.Name)))
                    .OrderByDescending(t => t.Version)
                    .Take(MaxUnreleasedTags)
                    .Select(t => t.Name)
                    .ToList();
            }, errorTitle: "Could not load releases").ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_load, cts))
            {
                IsLoading = false;
                IsEmpty = Error is null && Releases.Count == 0;
            }
        }
    }

    protected override async Task OnReadyAsync()
    {
        if (!_hasLoaded || _stale)
        {
            await LoadAsync().ConfigureAwait(true);
        }

        if (_pendingNavigation is { } navigation)
        {
            _pendingNavigation = null;
            if (navigation.StartNewRelease)
            {
                await StartWizardAsync(navigation.Version).ConfigureAwait(true);
            }
        }
    }

    protected override void OnNoLongerReady()
    {
        _load?.Cancel();
        CloseWizard();
        OnPropertyChanged(nameof(IsListVisible));
    }

    protected override void OnRepositoryChanged()
    {
        _load?.Cancel();
        CloseWizard();
        Releases.Clear();
        UnreleasedTags = [];
        IsEmpty = false;
        _hasLoaded = false;
    }

    protected override void OnDisposed()
    {
        Context.RepositoryChanged -= OnGitRepositoryChanged;
        _load?.Cancel();
        _load?.Dispose();
        Wizard?.Dispose();
    }

    protected override void OnAvailabilityUpdated() => OnPropertyChanged(nameof(IsListVisible));

    private async Task StartWizardAsync(string? version)
    {
        if (!IsReady || IsDisposed)
        {
            return;
        }

        if (Wizard is { } open)
        {
            if (open.IsExecuting)
            {
                return;
            }

            open.Dispose();
        }

        var wizard = new ReleaseWizardViewModel(Context, Services, _releaseService, GitHub, Releases.Select(r => r.TagName), version, OnWizardClosed);
        wizard.Published += OnPublished;
        Wizard = wizard;
        await wizard.InitializeAsync().ConfigureAwait(true);
    }

    private void OnWizardClosed(ReleaseWizardViewModel wizard)
    {
        if (ReferenceEquals(Wizard, wizard))
        {
            CloseWizard();
        }
    }

    private void CloseWizard()
    {
        if (Wizard is { } wizard)
        {
            wizard.Published -= OnPublished;
            wizard.Dispose();
            Wizard = null;
        }
    }

    private void OnPublished(object? sender, GitHubRelease? release)
    {
        if (Repository is { } repo)
        {
            GitHub.InvalidateCache(repo);
        }

        _ = LoadAsync();
    }

    /// <summary>Tags change with git operations (a tag created or fetched): the unreleased hint is stale.</summary>
    private void OnGitRepositoryChanged(object? sender, EventArgs e)
    {
        if (!_hasLoaded || IsDisposed)
        {
            return;
        }

        if (IsActive && IsReady && Wizard is null)
        {
            _ = LoadAsync();
        }
        else
        {
            _stale = true;
        }
    }

    private async Task<IReadOnlyList<Core.Git.GitTag>> LocalTagsAsync(CancellationToken token)
    {
        if (Context.GitStatus is null)
        {
            return [];
        }

        try
        {
            return await Services.Git.GetTagsAsync(Context.Root, token).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            // The hint is optional: the releases still show without it.
            return [];
        }
    }
}
