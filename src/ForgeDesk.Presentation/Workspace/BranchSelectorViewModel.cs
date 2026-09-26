using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>
/// The branch dropdown of the workspace header: searchable local and remote branches, switching
/// (offering to stash uncommitted changes when git refuses), and creating a branch.
/// </summary>
public sealed partial class BranchSelectorViewModel : ObservableObject, IDisposable
{
    private readonly ProjectContext _context;
    private readonly WorkspaceServices _services;
    private readonly WorkspaceActivity _activity;
    private readonly ILogger _logger;
    private IReadOnlyList<BranchItemViewModel> _allLocal = [];
    private IReadOnlyList<BranchItemViewModel> _allRemote = [];
    private bool _disposed;

    public BranchSelectorViewModel(ProjectContext context, WorkspaceServices services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        _context = context;
        _services = services;
        _activity = new WorkspaceActivity(services.Activity, context, services.Time);
        _logger = services.LoggerFactory.CreateLogger<BranchSelectorViewModel>();
        _context.GitStatusChanged += OnGitStatusChanged;
        _context.RepositoryChanged += OnRepositoryChanged;
        UpdateCurrent();
    }

    /// <summary>Name shown on the dropdown button: the branch, "abc1234 (detached)", or "No commits yet".</summary>
    [ObservableProperty]
    public partial string? CurrentBranchDisplay { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBranchCommand), nameof(CheckoutCommand))]
    public partial bool IsRepository { get; private set; }

    [ObservableProperty]
    public partial bool IsDetached { get; private set; }

    /// <summary>Whether the dropdown is open; opening it (re)loads the branches.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    public partial ErrorInfo? LoadError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBranchCommand), nameof(CheckoutCommand))]
    public partial bool IsSwitching { get; private set; }

    public ObservableCollection<BranchItemViewModel> LocalBranches { get; } = [];

    /// <summary>Remote branches that have no local branch yet ("Checkout remote…" section).</summary>
    public ObservableCollection<BranchItemViewModel> RemoteBranches { get; } = [];

    public bool HasRemoteBranches => RemoteBranches.Count > 0;

    public bool HasNoMatches => !IsLoading && LoadError is null && LocalBranches.Count == 0 && RemoteBranches.Count == 0;

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            FilterText = string.Empty;
            _ = LoadAsync();
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!_context.IsGitRepository)
        {
            _allLocal = [];
            _allRemote = [];
            ApplyFilter();
            return;
        }

        IsLoading = true;
        LoadError = null;
        try
        {
            var branches = await _services.Git.GetBranchesAsync(_context.Root, includeRemote: true, _context.Lifetime).ConfigureAwait(true);
            (_allLocal, _allRemote) = Split(branches);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoadError = ErrorInfo.From(ex, "Could not list branches");
            _allLocal = [];
            _allRemote = [];
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsLoading = false;
        }

        ApplyFilter();
    }

    /// <summary>Enter in the search box: switches to the single (or first) matching branch.</summary>
    [RelayCommand]
    private Task CheckoutFirstMatchAsync()
    {
        var first = LocalBranches.FirstOrDefault(b => !b.IsCurrent) ?? RemoteBranches.FirstOrDefault();
        return first is null || !CanChangeBranch() ? Task.CompletedTask : CheckoutAsync(first);
    }

    [RelayCommand(CanExecute = nameof(CanChangeBranch))]
    private async Task CheckoutAsync(BranchItemViewModel? branch)
    {
        if (branch is null || branch.IsCurrent)
        {
            IsOpen = false;
            return;
        }

        IsOpen = false;
        IsSwitching = true;
        var targetName = branch.IsRemote ? branch.LocalName : branch.Name;
        var stashed = false;
        try
        {
            var previous = _context.GitStatus?.Branch;
            try
            {
                await SwitchAsync(branch).ConfigureAwait(true);
            }
            catch (ForgeException ex) when (ex.Kind == ErrorKind.DirtyWorkingTree)
            {
                if (!await ConfirmStashAsync(targetName, previous).ConfigureAwait(true))
                {
                    return;
                }

                await _services.Git.StashAsync(_context.Root, $"ForgeDesk: changes on {previous ?? "HEAD"} before switching to {targetName}",
                    includeUntracked: true, _context.Lifetime).ConfigureAwait(true);
                stashed = true;
                await _activity.SucceededAsync(ActivityKind.GitStash, $"Stashed changes on {previous ?? "HEAD"}",
                    $"Before switching to {targetName}.", previous).ConfigureAwait(true);
                await SwitchAsync(branch).ConfigureAwait(true);
            }

            await _context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await _activity.SucceededAsync(ActivityKind.GitCheckout, $"Switched to {targetName}",
                previous is null ? null : $"From {previous}.", targetName).ConfigureAwait(true);
            _services.Notifications.Show($"Switched to {targetName}",
                stashed ? "Your uncommitted changes were stashed. Restore them from the stash list in the Git tab." : null,
                NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed while switching.
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, $"Could not switch to {targetName}");
            if (stashed)
            {
                error = error with { Hint = "Your changes are safe in the stash; restore them from the Git tab." };
                await _context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            }

            await _activity.FailedAsync(ActivityKind.GitCheckout, $"Could not switch to {targetName}", error, targetName).ConfigureAwait(true);
            _services.Notifications.ShowError(error);
        }
        finally
        {
            IsSwitching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeBranch))]
    private async Task CreateBranchAsync()
    {
        IsOpen = false;
        var from = _context.GitStatus?.Branch ?? "the current commit";
        var existing = _allLocal.Select(b => b.Name).ToArray();
        var name = await _services.Dialogs.PromptAsync(new PromptOptions
        {
            Title = "Create branch",
            Message = $"The new branch starts from {from} and becomes the current branch. Uncommitted changes come along.",
            Placeholder = "feature/short-description",
            ConfirmText = "Create branch",
            Validate = value => BranchNames.Validate(value, existing),
        }).ConfigureAwait(true);

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        IsSwitching = true;
        try
        {
            await _services.Git.CreateBranchAsync(_context.Root, trimmed, startPoint: null, checkout: true, _context.Lifetime).ConfigureAwait(true);
            await _context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await _activity.SucceededAsync(ActivityKind.GitBranchCreated, $"Created branch {trimmed}", $"From {from}.", trimmed).ConfigureAwait(true);
            _services.Notifications.Show($"Created and switched to {trimmed}", "Push to publish it when you are ready.", NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed while the branch was being created.
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, "Could not create the branch");
            await _activity.FailedAsync(ActivityKind.GitBranchCreated, $"Could not create branch {trimmed}", error, trimmed).ConfigureAwait(true);
            _services.Notifications.ShowError(error);
        }
        finally
        {
            IsSwitching = false;
        }
    }

    private bool CanChangeBranch() => IsRepository && !IsSwitching;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.GitStatusChanged -= OnGitStatusChanged;
        _context.RepositoryChanged -= OnRepositoryChanged;
    }

    private Task SwitchAsync(BranchItemViewModel branch) => branch.IsRemote
        ? _services.Git.CheckoutRemoteBranchAsync(_context.Root, branch.Name, null, _context.Lifetime)
        : _services.Git.CheckoutAsync(_context.Root, branch.Name, _context.Lifetime);

    private Task<bool> ConfirmStashAsync(string target, string? current) =>
        _services.Dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = "Uncommitted changes",
            Message = $"Switching to {target} would overwrite changes you have not committed"
                + (current is null ? "." : $" on {current}.")
                + "\n\nStash them to switch now. They stay safe in the stash list of the Git tab, ready to be restored.",
            ConfirmText = "Stash and switch",
            CancelText = "Cancel",
        });

    private void OnGitStatusChanged(object? sender, EventArgs e) => UpdateCurrent();

    private void OnRepositoryChanged(object? sender, EventArgs e)
    {
        if (IsOpen)
        {
            _ = LoadAsync();
        }
    }

    private void UpdateCurrent()
    {
        var status = _context.GitStatus;
        IsRepository = status is not null;
        IsDetached = status?.IsDetached == true;
        CurrentBranchDisplay = DisplayName(status);
    }

    /// <summary>What to call HEAD: the branch, "a1b2c3d (detached)", or "No commits yet".</summary>
    public static string? DisplayName(GitStatus? status) => status switch
    {
        null => null,
        { Branch: { } branch } => branch,
        { IsUnborn: true } => "No commits yet",
        { HeadSha: { Length: > 0 } sha } => $"{sha[..Math.Min(7, sha.Length)]} (detached)",
        _ => "Detached HEAD",
    };

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        LocalBranches.SyncWith(_allLocal.Where(b => Matches(b, filter)).ToList());
        RemoteBranches.SyncWith(_allRemote.Where(b => Matches(b, filter)).ToList());
        OnPropertyChanged(nameof(HasRemoteBranches));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private static bool Matches(BranchItemViewModel branch, string filter) =>
        filter.Length == 0 || branch.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Local branches (current first, then most recent) and the remote branches worth checking out:
    /// not a symbolic HEAD and not already present locally.
    /// </summary>
    internal static (IReadOnlyList<BranchItemViewModel> Local, IReadOnlyList<BranchItemViewModel> Remote) Split(IReadOnlyList<GitBranch> branches)
    {
        var local = branches.Where(b => !b.IsRemote)
            .OrderByDescending(b => b.IsCurrent)
            .ThenByDescending(b => b.TipDate ?? DateTimeOffset.MinValue)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => new BranchItemViewModel(b))
            .ToList();

        var tracked = new HashSet<string>(branches.Where(b => !b.IsRemote && b.Upstream is not null).Select(b => b.Upstream!), StringComparer.Ordinal);
        var localNames = new HashSet<string>(local.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        var remote = branches.Where(b => b.IsRemote && !b.Name.EndsWith("/HEAD", StringComparison.Ordinal))
            .Select(b => new BranchItemViewModel(b))
            .Where(b => !tracked.Contains(b.Name) && !localNames.Contains(b.LocalName))
            .OrderByDescending(b => b.TipDate ?? DateTimeOffset.MinValue)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (local, remote);
    }
}
