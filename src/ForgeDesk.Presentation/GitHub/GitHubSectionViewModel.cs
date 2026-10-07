using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>An entry of the GitHub tab's sub-navigation (Overview · Pull requests · Issues · Actions).</summary>
public sealed partial class GitHubSubViewTab : ObservableObject
{
    internal GitHubSubViewTab(GitHubView view, string title, string icon, string toolTip, GitHubSubViewModel viewModel)
    {
        View = view;
        Title = title;
        Icon = icon;
        ToolTip = toolTip;
        ViewModel = viewModel;
    }

    public GitHubView View { get; }

    public string Title { get; }

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon { get; }

    public string ToolTip { get; }

    public GitHubSubViewModel ViewModel { get; }

    /// <summary>UI automation id of the tab ("GitHub.PullRequestsTab").</summary>
    public string AutomationId => $"GitHub.{View}Tab";

    /// <summary>The view on screen.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; internal set; }
}

/// <summary>
/// The GitHub tab of a project workspace: the preconditions (linked, signed in, enabled) and a
/// sub-navigation over Overview, Pull requests, Issues and Actions. Views load when first shown;
/// F5 drops GitHub's cached responses for the repository and reloads the view on screen.
/// </summary>
public sealed partial class GitHubSectionViewModel : GitHubLinkedSectionViewModel, INavigationTarget, IRefreshable
{
    private readonly ILogger _logger;
    private GitHubSubViewTab? _current;
    private bool _suppressTabSelection;
    private string? _branch;

    public GitHubSectionViewModel(ProjectContext context, WorkspaceServices services, IGitHubService gitHub, IGitHubAccountService accounts)
        : base(context, services, gitHub, accounts)
    {
        _logger = services.LoggerFactory.CreateLogger<GitHubSectionViewModel>();
        var section = new GitHubSectionContext(context, services, gitHub, NavigateAsync);
        Overview = new GitHubOverviewViewModel(section);
        PullRequests = new PullRequestsViewModel(section);
        Issues = new IssuesViewModel(section);
        Actions = new ActionsViewModel(section);
        Tabs =
        [
            new GitHubSubViewTab(GitHubView.Overview, "Overview", "Home16", "Repository, CI health and counts", Overview),
            new GitHubSubViewTab(GitHubView.PullRequests, "Pull requests", "BranchRequest16", "Pull requests: review, check out, create", PullRequests),
            new GitHubSubViewTab(GitHubView.Issues, "Issues", "Record16", "Issues: discuss, close, turn into tasks", Issues),
            new GitHubSubViewTab(GitHubView.Actions, "Actions", "Flow16", "GitHub Actions workflow runs", Actions),
        ];

        _branch = section.CurrentBranch;
        Context.GitStatusChanged += OnGitStatusChanged;
        Show(Tabs[0]);
    }

    public override WorkspaceSection Section => WorkspaceSection.GitHub;

    public GitHubOverviewViewModel Overview { get; }

    public PullRequestsViewModel PullRequests { get; }

    public IssuesViewModel Issues { get; }

    public ActionsViewModel Actions { get; }

    public ObservableCollection<GitHubSubViewTab> Tabs { get; }

    /// <summary>Views shown at least once; the view keeps them alive so lists keep their scroll and selection.</summary>
    public ObservableCollection<GitHubSubViewTab> OpenedTabs { get; } = [];

    [ObservableProperty]
    public partial GitHubSubViewTab? SelectedTab { get; set; }

    [ObservableProperty]
    public partial GitHubView CurrentView { get; private set; }

    public GitHubSubViewModel? CurrentSubView => _current?.ViewModel;

    /// <summary>Deep link: a <see cref="GitHubNavigation"/> or a <see cref="GitHubView"/>.</summary>
    public async Task NavigateToAsync(object argument)
    {
        if (GitHubNavigation.From(argument) is { } navigation)
        {
            await NavigateAsync(navigation).ConfigureAwait(true);
        }
    }

    /// <summary>Shows a view of the tab and applies the rest of the link to it.</summary>
    public async Task NavigateAsync(GitHubNavigation navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        if (IsDisposed)
        {
            return;
        }

        var target = Tabs[(int)navigation.View];
        target.ViewModel.PrepareNavigation(navigation);
        await SelectViewAsync(navigation.View).ConfigureAwait(true);
        if (IsReady && IsActive)
        {
            await target.ViewModel.NavigateAsync(navigation).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task SelectViewAsync(GitHubView view)
    {
        if (IsDisposed)
        {
            return;
        }

        var tab = Tabs[(int)view];
        if (ReferenceEquals(_current, tab))
        {
            SelectTabSilently(tab);
            return;
        }

        _current?.ViewModel.Deactivate();
        Show(tab);
        if (IsActive && IsReady)
        {
            await tab.ViewModel.ActivateAsync().ConfigureAwait(true);
        }
    }

    /// <summary>F5: drops GitHub's cached responses for the repository and reloads the view on screen.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsReady || Repository is not { } repo)
        {
            return;
        }

        try
        {
            GitHub.InvalidateCache(repo);
            foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, _current)))
            {
                tab.ViewModel.Invalidate();
            }

            if (_current?.ViewModel is { } view)
            {
                if (view.HasLoaded)
                {
                    await view.LoadAsync().ConfigureAwait(true);
                }
                else if (IsActive)
                {
                    await view.ActivateAsync().ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
    }

    protected override async Task OnReadyAsync()
    {
        if (_current is { } current)
        {
            await current.ViewModel.ActivateAsync().ConfigureAwait(true);
        }
    }

    protected override void OnDeactivated() => _current?.ViewModel.Deactivate();

    protected override void OnNoLongerReady() => _current?.ViewModel.Deactivate();

    protected override void OnRepositoryChanged()
    {
        foreach (var tab in Tabs)
        {
            tab.ViewModel.Deactivate();
            tab.ViewModel.Reset();
        }
    }

    protected override void OnDisposed()
    {
        Context.GitStatusChanged -= OnGitStatusChanged;
        foreach (var tab in Tabs)
        {
            try
            {
                tab.ViewModel.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The GitHub {View} view failed to dispose", tab.View);
            }
        }
    }

    partial void OnSelectedTabChanged(GitHubSubViewTab? value)
    {
        if (!_suppressTabSelection && value is not null)
        {
            _ = SelectViewAsync(value.View);
        }
    }

    private void Show(GitHubSubViewTab tab)
    {
        if (_current is { } previous)
        {
            previous.IsCurrent = false;
        }

        _current = tab;
        tab.IsCurrent = true;
        if (!OpenedTabs.Contains(tab))
        {
            OpenedTabs.Add(tab);
        }

        CurrentView = tab.View;
        OnPropertyChanged(nameof(CurrentSubView));
        SelectTabSilently(tab);
    }

    private void SelectTabSilently(GitHubSubViewTab tab)
    {
        _suppressTabSelection = true;
        try
        {
            SelectedTab = tab;
        }
        finally
        {
            _suppressTabSelection = false;
        }
    }

    /// <summary>A checkout changes the "current branch" CI card and the Actions "Current branch" scope.</summary>
    private void OnGitStatusChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        var branch = Context.GitStatus is { IsDetached: false, Branch: { Length: > 0 } b } ? b : null;
        if (string.Equals(branch, _branch, StringComparison.Ordinal))
        {
            return;
        }

        _branch = branch;
        Overview.Invalidate();
        Actions.OnBranchChanged();
    }
}
