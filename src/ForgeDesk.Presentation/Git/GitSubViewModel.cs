using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Git;

/// <summary>What the views of the Git tab share: the project, the services, the journal and navigation between views.</summary>
public sealed class GitSectionContext
{
    internal GitSectionContext(ProjectContext context, WorkspaceServices services, GitViewPreferences preferences, Func<GitNavigation, Task> navigate)
    {
        Context = context;
        Services = services;
        Preferences = preferences;
        Navigate = navigate;
        Activity = new GitActivity(services.Activity, context, services.Time);
    }

    public ProjectContext Context { get; }

    public WorkspaceServices Services { get; }

    public GitViewPreferences Preferences { get; }

    /// <summary>Shows another view of the Git tab (with its argument).</summary>
    public Func<GitNavigation, Task> Navigate { get; }

    internal GitActivity Activity { get; }

    public IGitService Git => Services.Git;

    public string Root => Context.Root;

    public CancellationToken Lifetime => Context.Lifetime;

    public IDialogService Dialogs => Services.Dialogs;

    public INotificationService Notifications => Services.Notifications;

    public IShellIntegration Shell => Services.Shell;
}

/// <summary>
/// One view of the Git tab (Changes, History, Branches, Stashes, Tags). Loads on first activation,
/// reloads when invalidated while visible, and only remembers that it is stale while hidden.
/// </summary>
public abstract partial class GitSubViewModel : ViewModelBase, IDisposable
{
    private bool _stale;
    private Task? _invalidationLoad;

    protected GitSubViewModel(GitSectionContext section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Section = section;
    }

    public abstract GitView View { get; }

    protected GitSectionContext Section { get; }

    protected ProjectContext Context => Section.Context;

    protected IGitService Git => Section.Git;

    protected string Root => Section.Root;

    /// <summary>True while this view is on screen.</summary>
    public bool IsActive { get; private set; }

    /// <summary>True once the first load was started.</summary>
    public bool HasLoaded { get; private set; }

    /// <summary>First load in progress, nothing to show yet: the view shows skeleton rows.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; protected set; }

    protected bool IsDisposed { get; private set; }

    internal async Task ActivateAsync()
    {
        IsActive = true;
        if (!HasLoaded || _stale)
        {
            _stale = false;
            HasLoaded = true;
            await LoadAsync().ConfigureAwait(true);
        }
        else
        {
            OnReactivated();
        }
    }

    internal void Deactivate()
    {
        IsActive = false;
        OnDeactivated();
    }

    /// <summary>The data changed: reload now when visible, on the next activation otherwise.</summary>
    internal void Invalidate()
    {
        if (!HasLoaded || IsDisposed)
        {
            return;
        }

        if (IsActive)
        {
            // Several signals often follow one operation (status, then refs): a load already
            // running started after the change, so it reads the new state.
            if (_invalidationLoad is { IsCompleted: false })
            {
                return;
            }

            _invalidationLoad = LoadAsync();
        }
        else
        {
            _stale = true;
        }
    }

    /// <summary>Loads (or reloads) the view's data. Never throws: failures go to <see cref="ViewModelBase.Error"/>.</summary>
    public abstract Task LoadAsync();

    /// <summary>Retry of the error panel and "Refresh" menu entries.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    /// <summary>Takes the filters of a deep link before the view is shown, so it loads once with them.</summary>
    internal virtual void PrepareNavigation(GitNavigation navigation)
    {
    }

    /// <summary>Applies a deep link once the view is selected and loaded (select an item, focus a field…).</summary>
    internal virtual Task NavigateAsync(GitNavigation navigation) => Task.CompletedTask;

    protected virtual void OnReactivated()
    {
    }

    protected virtual void OnDeactivated()
    {
    }

    /// <summary>Runs an action whose failure is reported as a notification (never inline).</summary>
    protected Task<bool> RunActionAsync(Func<Task> work, string errorTitle) =>
        RunAsync(work, errorTitle: errorTitle, errorMode: ErrorMode.Toast, notifications: Section.Notifications);

    protected void Notify(string title, string? message = null, NotificationSeverity severity = NotificationSeverity.Success) =>
        Section.Notifications.Show(title, message, severity);

    protected void ShowError(Exception exception, string title) =>
        Section.Notifications.ShowError(ErrorInfo.From(exception, exception is ForgeException { Kind: not ErrorKind.Unknown } ? null : title));

    /// <summary>Copies text to the clipboard and confirms it.</summary>
    protected void Copy(string text, string what)
    {
        try
        {
            Section.Shell.CopyToClipboard(text);
            Notify($"{what} copied", text.Length > 120 ? text[..117] + "…" : text, NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            Section.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        OnDisposed();
        GC.SuppressFinalize(this);
    }

    /// <summary>Cancels the view's pending work (called once).</summary>
    protected virtual void OnDisposed()
    {
    }
}
