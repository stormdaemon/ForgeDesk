using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.GitHub;

/// <summary>Whether a workspace tab can talk to GitHub for the project, and if not, why.</summary>
public enum GitHubAvailability
{
    /// <summary>GitHub features are turned off in Settings.</summary>
    Disabled,

    /// <summary>The project has no GitHub remote.</summary>
    NotLinked,

    /// <summary>No GitHub account is signed in.</summary>
    SignedOut,

    Ready,
}

/// <summary>
/// Base of the workspace tabs that work on the project's GitHub repository (GitHub, Releases): the
/// preconditions (GitHub enabled, project linked, account signed in), their fixes (re-detect the
/// remote, sign in, turn GitHub back on) and activation tracking. Derived tabs load their data in
/// <see cref="OnReadyAsync"/>, called whenever the tab is visible and becomes usable.
/// </summary>
public abstract partial class GitHubLinkedSectionViewModel : ViewModelBase, IWorkspaceSectionViewModel, IDisposable
{
    private GitHubRepoRef? _repo;

    protected GitHubLinkedSectionViewModel(ProjectContext context, WorkspaceServices services, IGitHubService gitHub, IGitHubAccountService accounts)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gitHub);
        ArgumentNullException.ThrowIfNull(accounts);
        Context = context;
        Services = services;
        GitHub = gitHub;
        Accounts = accounts;
        _repo = context.Project.GitHub;

        Context.PropertyChanged += OnContextPropertyChanged;
        services.Settings.Changed += OnSettingsChanged;
        accounts.AccountChanged += OnAccountChanged;
        Availability = Evaluate();
    }

    public abstract WorkspaceSection Section { get; }

    public ProjectContext Context { get; }

    protected WorkspaceServices Services { get; }

    protected IGitHubService GitHub { get; }

    protected IGitHubAccountService Accounts { get; }

    protected bool IsDisposed { get; private set; }

    /// <summary>True while the tab is on screen.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The linked repository, or null when the project has no GitHub remote.</summary>
    public GitHubRepoRef? Repository => Context.Project.GitHub;

    /// <summary>"owner/name" of the linked repository.</summary>
    public string? RepositoryName => Repository?.FullName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisabled), nameof(IsNotLinked), nameof(IsSignedOut), nameof(IsReady))]
    public partial GitHubAvailability Availability { get; private set; }

    public bool IsDisabled => Availability == GitHubAvailability.Disabled;

    public bool IsNotLinked => Availability == GitHubAvailability.NotLinked;

    public bool IsSignedOut => Availability == GitHubAvailability.SignedOut;

    public bool IsReady => Availability == GitHubAvailability.Ready;

    [ObservableProperty]
    public partial bool IsRedetecting { get; private set; }

    public string NotLinkedDescription =>
        $"ForgeDesk links a project to GitHub through its \"origin\" remote. {Context.Project.Name} has no remote pointing to github.com. "
        + "Add one (for example with git remote add origin https://github.com/owner/repo.git), then re-detect.";

    public string SignedOutDescription =>
        "Sign in to see pull requests, issues, Actions runs and releases of " + (RepositoryName ?? "this repository") + ".";

    public string DisabledDescription =>
        "GitHub features are turned off in Settings. Turn them back on to see pull requests, issues, Actions runs and releases.";

    public async Task ActivateAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsActive = true;
        UpdateAvailability(notify: false);
        if (IsReady)
        {
            await OnReadyAsync().ConfigureAwait(true);
        }
    }

    public void Deactivate()
    {
        IsActive = false;
        OnDeactivated();
    }

    /// <summary>The tab is visible and GitHub is usable: load (or refresh stale) data.</summary>
    protected abstract Task OnReadyAsync();

    protected virtual void OnDeactivated()
    {
    }

    /// <summary>GitHub stopped being usable (signed out, disabled, unlinked): drop pending work.</summary>
    protected virtual void OnNoLongerReady()
    {
    }

    /// <summary>Called after <see cref="Availability"/> changed (for properties derived from it).</summary>
    protected virtual void OnAvailabilityUpdated()
    {
    }

    partial void OnAvailabilityChanged(GitHubAvailability value) => OnAvailabilityUpdated();

    /// <summary>The project was linked to another repository: everything loaded is stale.</summary>
    protected virtual void OnRepositoryChanged()
    {
    }

    /// <summary>Re-reads the git remotes and links the project to the GitHub repository found.</summary>
    [RelayCommand]
    private async Task RedetectAsync()
    {
        IsRedetecting = true;
        try
        {
            await RunAsync(async () =>
            {
                var remotes = await Services.Git.GetRemotesAsync(Context.Root, Context.Lifetime).ConfigureAwait(true);
                var repo = GitHubLinks.Pick(remotes);
                if (repo is null)
                {
                    Services.Notifications.Show("No GitHub remote found",
                        remotes.Count == 0
                            ? $"{Context.Project.Name} has no git remote. Add one that points to github.com, then re-detect."
                            : $"None of the remotes of {Context.Project.Name} ({string.Join(", ", remotes.Select(r => r.Name))}) points to github.com.",
                        NotificationSeverity.Warning);
                    return;
                }

                var updated = await Services.Registry.UpdateAsync(Context.Project with { GitHub = repo }, Context.Lifetime).ConfigureAwait(true);
                Context.Project = updated;
                Services.Notifications.Show($"Linked to {repo.FullName}", $"{updated.Name} now shows the pull requests, issues and runs of {repo.FullName}.",
                    NotificationSeverity.Success);
            }, "Detecting the GitHub remote…", "Could not read the git remotes", ErrorMode.Toast, Services.Notifications).ConfigureAwait(true);
        }
        finally
        {
            IsRedetecting = false;
        }
    }

    [RelayCommand]
    private void SignIn() => Services.Navigation.OpenSettings("GitHub");

    [RelayCommand]
    private Task EnableGitHubAsync() => RunAsync(
        () => Services.Settings.UpdateAsync(s => s with { GitHubEnabled = true }, Context.Lifetime),
        errorTitle: "Could not turn GitHub features on", errorMode: ErrorMode.Toast, notifications: Services.Notifications);

    [RelayCommand]
    private void OpenRepositoryOnGitHub()
    {
        if (Repository is { } repo)
        {
            OpenUrl(repo.HtmlUrl);
        }
    }

    protected void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Services.Shell.OpenUrl(url);
        }
        catch (Exception ex)
        {
            Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the browser"));
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        IsActive = false;
        Context.PropertyChanged -= OnContextPropertyChanged;
        Services.Settings.Changed -= OnSettingsChanged;
        Accounts.AccountChanged -= OnAccountChanged;
        OnDisposed();
        GC.SuppressFinalize(this);
    }

    protected virtual void OnDisposed()
    {
    }

    /// <summary>Re-evaluates the preconditions and reacts to a change (load when ready and visible).</summary>
    protected void UpdateAvailability(bool notify = true)
    {
        if (IsDisposed)
        {
            return;
        }

        var previous = Availability;
        Availability = Evaluate();
        if (!notify || previous == Availability)
        {
            return;
        }

        if (previous == GitHubAvailability.Ready)
        {
            OnNoLongerReady();
        }
        else if (IsReady && IsActive)
        {
            _ = OnReadyAsync();
        }
    }

    private GitHubAvailability Evaluate()
    {
        AppSettings settings;
        try
        {
            settings = Services.Settings.Current ?? AppSettings.Default;
        }
        catch (Exception)
        {
            settings = AppSettings.Default;
        }

        return !settings.GitHubEnabled ? GitHubAvailability.Disabled
            : Context.Project.GitHub is null ? GitHubAvailability.NotLinked
            : !IsSignedIn() ? GitHubAvailability.SignedOut
            : GitHubAvailability.Ready;
    }

    private bool IsSignedIn() => Accounts.Current is not null || GitHub.IsSignedIn;

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectContext.Project) || IsDisposed)
        {
            return;
        }

        OnPropertyChanged(nameof(Repository));
        OnPropertyChanged(nameof(RepositoryName));
        OnPropertyChanged(nameof(NotLinkedDescription));
        OnPropertyChanged(nameof(SignedOutDescription));
        var repo = Context.Project.GitHub;
        if (repo != _repo)
        {
            _repo = repo;
            OnRepositoryChanged();
        }

        UpdateAvailability();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => Services.Dispatcher.Post(() => UpdateAvailability());

    private void OnAccountChanged(object? sender, GitHubAccount? account) => Services.Dispatcher.Post(() =>
    {
        if (IsDisposed)
        {
            return;
        }

        OnPropertyChanged(nameof(SignedOutDescription));
        UpdateAvailability();
    });
}
