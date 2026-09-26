using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Git;

/// <summary>
/// The Branches view: local branches (current first) and remote branches grouped by remote, with
/// switching, creating, renaming, deleting, merging, publishing and bulk deletion of merged branches.
/// </summary>
public sealed partial class BranchesViewModel : GitSubViewModel
{
    /// <summary>Long-lived branch names never offered for bulk deletion, whatever the default branch is.</summary>
    private static readonly string[] ProtectedNames = ["main", "master"];

    private IReadOnlyList<BranchRowViewModel> _local = [];
    private IReadOnlyList<BranchRowViewModel> _remote = [];
    private string? _selectAfterLoad;

    public BranchesViewModel(GitSectionContext section)
        : base(section)
    {
    }

    public override GitView View => GitView.Branches;

    public ObservableCollection<BranchListRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial BranchRowViewModel? SelectedBranch { get; set; }

    public bool HasSelection => SelectedBranch is not null;

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int LocalCount { get; private set; }

    [ObservableProperty]
    public partial int RemoteCount { get; private set; }

    [ObservableProperty]
    public partial string? DefaultBranch { get; private set; }

    /// <summary>Name of the checked-out branch (merge target), null when HEAD is detached.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MergeTargetText))]
    public partial string? CurrentBranch { get; private set; }

    /// <summary>"Merge into main".</summary>
    public string MergeTargetText => CurrentBranch is { } current ? $"Merge into {current}" : "Merge into current branch";

    [ObservableProperty]
    public partial bool HasNoMatches { get; private set; }

    /// <summary>A repository without commits has no branch yet.</summary>
    [ObservableProperty]
    public partial bool HasNoBranches { get; private set; }

    public string NoBranchesDescription => Context.GitStatus?.Branch is { } branch
        ? $"The first commit creates the branch {branch}. Make it from Changes."
        : "Branches appear here once the repository has commits.";

    /// <summary>A branch operation is running (switching, merging…): the header shows progress.</summary>
    [ObservableProperty]
    public partial bool IsWorking { get; private set; }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public override async Task LoadAsync()
    {
        var previous = SelectedBranch?.FullName;
        IsLoading = Rows.Count == 0;
        try
        {
            await RunAsync(async () =>
            {
                var branchesTask = Git.GetBranchesAsync(Root, includeRemote: true, Section.Lifetime);
                var mergedTask = OptionalAsync(() => Git.GetMergedBranchesAsync(Root, null, Section.Lifetime), (IReadOnlyList<string>)[]);
                var defaultTask = OptionalAsync(() => Git.GetDefaultBranchAsync(Root, Section.Lifetime), (string?)null);
                var branches = await branchesTask.ConfigureAwait(true);
                var merged = (await mergedTask.ConfigureAwait(true)).ToHashSet(StringComparer.Ordinal);
                var defaultBranch = await defaultTask.ConfigureAwait(true);

                DefaultBranch = defaultBranch;
                CurrentBranch = branches.FirstOrDefault(b => b.IsCurrent && !b.IsRemote)?.Name ?? Context.GitStatus?.Branch;
                _local = branches.Where(b => !b.IsRemote)
                    .OrderByDescending(b => b.IsCurrent)
                    .ThenByDescending(b => string.Equals(b.Name, defaultBranch, StringComparison.Ordinal))
                    .ThenByDescending(b => b.TipDate ?? DateTimeOffset.MinValue)
                    .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(b => new BranchRowViewModel(b, merged.Contains(b.Name), string.Equals(b.Name, defaultBranch, StringComparison.Ordinal)))
                    .ToList();
                _remote = branches.Where(b => b.IsRemote && !b.Name.EndsWith("/HEAD", StringComparison.Ordinal))
                    .OrderBy(b => b.RemoteName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(b => new BranchRowViewModel(b))
                    .ToList();
                LocalCount = _local.Count;
                RemoteCount = _remote.Count;
                HasNoBranches = _local.Count + _remote.Count == 0;
                OnPropertyChanged(nameof(NoBranchesDescription));
                ApplyFilter();
                var select = _selectAfterLoad ?? previous;
                _selectAfterLoad = null;
                SelectedBranch = (select is null ? null : Rows.OfType<BranchRowViewModel>().FirstOrDefault(r => r.FullName == select || r.Name == select))
                                 ?? Rows.OfType<BranchRowViewModel>().FirstOrDefault();
            }, errorTitle: "Could not list the branches").ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal override Task NavigateAsync(GitNavigation navigation)
    {
        if (navigation.Name is { } name)
        {
            var match = Rows.OfType<BranchRowViewModel>().FirstOrDefault(r => r.Name == name || r.FullName == name);
            if (match is null && FilterText.Length > 0)
            {
                FilterText = string.Empty;
                match = Rows.OfType<BranchRowViewModel>().FirstOrDefault(r => r.Name == name || r.FullName == name);
            }

            if (match is not null)
            {
                SelectedBranch = match;
            }
        }

        return Task.CompletedTask;
    }

    // ----- Actions ----------------------------------------------------------------------

    /// <summary>Switches to a local branch, or checks a remote branch out as a new local tracking branch.</summary>
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private async Task SwitchAsync(BranchRowViewModel? row)
    {
        row ??= SelectedBranch;
        if (row is null || row.IsCurrent)
        {
            return;
        }

        var target = row.IsRemote ? row.LocalName : row.Name;
        var previous = Context.GitStatus?.Branch;
        var stashed = false;
        IsWorking = true;
        try
        {
            try
            {
                await SwitchCoreAsync(row).ConfigureAwait(true);
            }
            catch (ForgeException ex) when (ex.Kind == ErrorKind.DirtyWorkingTree)
            {
                if (!await ConfirmStashAsync(target, previous).ConfigureAwait(true))
                {
                    return;
                }

                await GitFlows.StashForSwitchAsync(Section, target).ConfigureAwait(true);
                stashed = true;
                await SwitchCoreAsync(row).ConfigureAwait(true);
            }

            _selectAfterLoad = row.IsRemote ? "refs/heads/" + target : row.FullName;
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.SucceededAsync(ActivityKind.GitCheckout, $"Switched to {target}", previous is null ? null : $"From {previous}.", target)
                .ConfigureAwait(true);
            Notify($"Switched to {target}", stashed ? "Your uncommitted changes were stashed. Restore them from the Stashes view." : null);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, $"Could not switch to {target}");
            if (stashed)
            {
                error = error with { Hint = "Your changes are safe in the stash; restore them from the Stashes view." };
                await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            }

            await Section.Activity.FailedAsync(ActivityKind.GitCheckout, $"Could not switch to {target}", error, target).ConfigureAwait(true);
            Section.Notifications.ShowError(error);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private static bool CanSwitch(BranchRowViewModel? row) => row is null || !row.IsCurrent;

    [RelayCommand]
    private Task NewBranchAsync(BranchRowViewModel? row)
    {
        var from = row ?? SelectedBranch;
        return from is null
            ? GitFlows.CreateBranchAsync(Section, null, CurrentBranch ?? "the current commit")
            : GitFlows.CreateBranchAsync(Section, from.Name, from.Name);
    }

    /// <summary>A new branch from the current HEAD (toolbar button).</summary>
    [RelayCommand]
    private Task NewBranchFromHeadAsync() => GitFlows.CreateBranchAsync(Section, null, CurrentBranch ?? "the current commit");

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync(BranchRowViewModel? row)
    {
        row ??= SelectedBranch;
        if (row is null || row.IsRemote)
        {
            return;
        }

        var others = _local.Where(b => !ReferenceEquals(b, row)).Select(b => b.Name).ToArray();
        string? name;
        try
        {
            name = await Section.Dialogs.PromptAsync(new PromptOptions
            {
                Title = $"Rename {row.Name}",
                Message = row.HasUpstream ? $"It keeps tracking {row.Upstream}; the branch on the remote keeps its current name." : null,
                InitialValue = row.Name,
                ConfirmText = "Rename",
                Validate = value => string.Equals(value?.Trim(), row.Name, StringComparison.Ordinal) ? "Enter a different name." : BranchNames.Validate(value, others),
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                ShowError(ex, "Could not rename the branch");
            }

            return;
        }

        var newName = name?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == row.Name)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            await Git.RenameBranchAsync(Root, row.Name, newName, Section.Lifetime).ConfigureAwait(true);
            _selectAfterLoad = "refs/heads/" + newName;
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            Notify($"Renamed {row.Name} to {newName}");
        }, "Could not rename the branch").ConfigureAwait(true);
    }

    private static bool CanRename(BranchRowViewModel? row) => row is null || row.IsLocal;

    /// <summary>Deletes a local branch; when git refuses because it is not fully merged, offers a force delete.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(BranchRowViewModel? row)
    {
        row ??= SelectedBranch;
        if (row is null || row.IsRemote || row.IsCurrent)
        {
            return;
        }

        var notes = new List<string> { $"The local branch {row.Name} will be deleted." };
        if (row.IsMerged)
        {
            notes.Add($"It is fully merged into {DefaultBranch}, so no work is lost.");
        }

        if (row.HasUpstream && !row.IsGone)
        {
            notes.Add($"The remote branch {row.Upstream} is not deleted.");
        }

        if (!await ConfirmAsync($"Delete branch {row.Name}?", string.Join("\n\n", notes), "Delete branch").ConfigureAwait(true))
        {
            return;
        }

        IsWorking = true;
        try
        {
            var forced = false;
            try
            {
                await Git.DeleteBranchAsync(Root, row.Name, force: false, Section.Lifetime).ConfigureAwait(true);
            }
            catch (ForgeException ex) when (ex.Kind == ErrorKind.GitCommandFailed)
            {
                // git branch -d refuses a branch whose commits are not merged anywhere.
                var force = await ConfirmAsync($"{row.Name} has unmerged commits",
                    $"{ex.Message}\n\nForce delete it anyway? Commits that no other branch contains will be lost "
                    + "(git keeps them in its reflog for a while).", "Force delete").ConfigureAwait(true);
                if (!force)
                {
                    return;
                }

                await Git.DeleteBranchAsync(Root, row.Name, force: true, Section.Lifetime).ConfigureAwait(true);
                forced = true;
            }

            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.SucceededAsync(ActivityKind.GitBranchDeleted, $"Deleted branch {row.Name}",
                forced ? "Force deleted: it had unmerged commits." : null, row.Name).ConfigureAwait(true);
            Notify($"Deleted {row.Name}", forced ? "It was force deleted." : null);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, ex is ForgeException { Kind: not ErrorKind.Unknown } ? null : $"Could not delete {row.Name}");
            await Section.Activity.FailedAsync(ActivityKind.GitBranchDeleted, $"Could not delete branch {row.Name}", error, row.Name).ConfigureAwait(true);
            Section.Notifications.ShowError(error);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private static bool CanDelete(BranchRowViewModel? row) => row is null || (row.IsLocal && !row.IsCurrent);

    /// <summary>Merges a branch into the current one; on conflicts, shows Changes where they are resolved.</summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync(BranchRowViewModel? row)
    {
        row ??= SelectedBranch;
        var current = Context.GitStatus?.Branch;
        if (row is null || row.IsCurrent)
        {
            return;
        }

        if (current is null)
        {
            Notify("You're not on a branch", "Switch to the branch that should receive the changes, then merge.", NotificationSeverity.Warning);
            return;
        }

        var confirmed = await ConfirmAsync($"Merge {row.Name} into {current}?",
            $"The commits of {row.Name} are merged into {current}, the current branch. If both changed the same lines, "
            + "you resolve the conflicts in Changes before the merge is committed.", "Merge", destructive: false).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await Git.MergeAsync(Root, row.Name, Section.Lifetime).ConfigureAwait(true);
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.SucceededAsync(ActivityKind.GitMerge, $"Merged {row.Name} into {current}", null, current).ConfigureAwait(true);
            Notify($"Merged {row.Name} into {current}");
        }
        catch (ForgeException ex) when (ex.Kind == ErrorKind.MergeConflict)
        {
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.WarningAsync(ActivityKind.GitMerge, $"Merge of {row.Name} into {current} has conflicts", ex.Message, current)
                .ConfigureAwait(true);
            Notify("The merge has conflicts", $"{ex.Message} Resolve them in Changes, then commit to complete the merge.", NotificationSeverity.Warning);
            await Section.Navigate(new GitNavigation(GitView.Changes) { CommitMessage = $"Merge branch '{row.Name}' into {current}" }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, ex is ForgeException { Kind: not ErrorKind.Unknown } ? null : $"Could not merge {row.Name}");
            await Section.Activity.FailedAsync(ActivityKind.GitMerge, $"Could not merge {row.Name} into {current}", error, current).ConfigureAwait(true);
            Section.Notifications.ShowError(error);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private static bool CanMerge(BranchRowViewModel? row) => row is null || !row.IsCurrent;

    /// <summary>Pushes a local branch that has no upstream yet and makes it track the remote branch.</summary>
    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task PublishAsync(BranchRowViewModel? row)
    {
        row ??= SelectedBranch;
        if (row is null || !row.NeedsPublish)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await Git.PushAsync(Root, new GitPushOptions(SetUpstream: true, Branch: row.Name), null, Section.Lifetime).ConfigureAwait(true);
            _selectAfterLoad = row.FullName;
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.SucceededAsync(ActivityKind.GitPush, $"Published {row.Name}", null, row.Name).ConfigureAwait(true);
            Notify($"Published {row.Name}", "It now has an upstream branch on the remote.");
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, ex is ForgeException { Kind: not ErrorKind.Unknown } ? null : $"Could not publish {row.Name}");
            await Section.Activity.FailedAsync(ActivityKind.GitPush, $"Could not publish {row.Name}", error, row.Name).ConfigureAwait(true);
            Section.Notifications.ShowError(error);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private static bool CanPublish(BranchRowViewModel? row) => row is null || row.NeedsPublish;

    /// <summary>Deletes every local branch fully merged into the default branch, except the current and default ones.</summary>
    [RelayCommand]
    private async Task DeleteMergedAsync()
    {
        IReadOnlyList<string> candidates;
        string? defaultBranch;
        try
        {
            defaultBranch = await Git.GetDefaultBranchAsync(Root, Section.Lifetime).ConfigureAwait(true);
            var merged = await Git.GetMergedBranchesAsync(Root, null, Section.Lifetime).ConfigureAwait(true);
            candidates = MergedBranchesToDelete(merged, Context.GitStatus?.Branch, defaultBranch);
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                ShowError(ex, "Could not find the merged branches");
            }

            return;
        }

        var into = defaultBranch ?? "the default branch";
        if (candidates.Count == 0)
        {
            Notify("No merged branches to delete", $"Every other local branch has commits that aren't in {into}.", NotificationSeverity.Info);
            return;
        }

        var confirmed = await ConfirmAsync($"Delete {Format.Count(candidates.Count, "merged branch", "merged branches")}?",
            $"These local branches are fully merged into {into}, so no work is lost:\n{ChangesViewModel.ListPaths(candidates)}\n\n"
            + "The current branch, the default branch and the branches on the remote are kept.",
            $"Delete {Format.Count(candidates.Count, "branch", "branches")}").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        IsWorking = true;
        var deleted = new List<string>();
        var failed = new List<(string Branch, ErrorInfo Error)>();
        try
        {
            foreach (var branch in candidates)
            {
                try
                {
                    await Git.DeleteBranchAsync(Root, branch, force: false, Section.Lifetime).ConfigureAwait(true);
                    deleted.Add(branch);
                }
                catch (Exception ex) when (!ex.IsCancellation())
                {
                    failed.Add((branch, ErrorInfo.From(ex)));
                }
            }

            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            if (deleted.Count > 0)
            {
                await Section.Activity.SucceededAsync(ActivityKind.GitBranchDeleted, $"Deleted {Format.Count(deleted.Count, "merged branch", "merged branches")}",
                    string.Join(", ", deleted)).ConfigureAwait(true);
            }

            if (failed.Count == 0)
            {
                Notify($"Deleted {Format.Count(deleted.Count, "merged branch", "merged branches")}", string.Join(", ", deleted));
            }
            else
            {
                Section.Notifications.ShowError(new ErrorInfo(ErrorKind.GitCommandFailed,
                    $"Deleted {deleted.Count} of {candidates.Count} branches",
                    $"Could not delete {string.Join(", ", failed.Select(f => f.Branch))}.",
                    failed[0].Error.Message,
                    string.Join('\n', failed.Select(f => $"{f.Branch}: {f.Error.Message}"))));
            }
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private Task ViewHistoryAsync(BranchRowViewModel? row)
    {
        var target = row ?? SelectedBranch;
        return target is null ? Task.CompletedTask : Section.Navigate(GitNavigation.BranchHistory(target.Name));
    }

    [RelayCommand]
    private void CopyName(BranchRowViewModel? row)
    {
        if ((row ?? SelectedBranch) is { } target)
        {
            Copy(target.Name, "Branch name");
        }
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    /// <summary>Merged local branches that bulk deletion may remove: never the current, default, main or master branch.</summary>
    internal static IReadOnlyList<string> MergedBranchesToDelete(IEnumerable<string> merged, string? current, string? defaultBranch) =>
        merged.Where(b => !string.Equals(b, current, StringComparison.Ordinal)
                          && !string.Equals(b, defaultBranch, StringComparison.Ordinal)
                          && !ProtectedNames.Contains(b, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private Task SwitchCoreAsync(BranchRowViewModel row) => row.IsRemote
        ? Git.CheckoutRemoteBranchAsync(Root, row.Name, null, Section.Lifetime)
        : Git.CheckoutAsync(Root, row.Name, Section.Lifetime);

    private Task<bool> ConfirmStashAsync(string target, string? current) =>
        Section.Dialogs.ConfirmAsync(new ConfirmOptions
        {
            Title = "Uncommitted changes",
            Message = $"Switching to {target} would overwrite changes you have not committed"
                + (current is null ? "." : $" on {current}.")
                + "\n\nStash them to switch now. They stay safe in the Stashes view, ready to be restored.",
            ConfirmText = "Stash and switch",
        });

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = true)
    {
        try
        {
            return await Section.Dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = title,
                Message = message,
                ConfirmText = confirmText,
                IsDestructive = destructive,
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                ShowError(ex, "Could not ask for confirmation");
            }

            return false;
        }
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        var rows = new List<BranchListRow>();
        var local = _local.Where(b => Matches(b, filter)).ToList();
        if (local.Count > 0)
        {
            rows.Add(new BranchGroupHeader("Local branches", local.Count, isRemote: false));
            rows.AddRange(local);
        }

        foreach (var group in _remote.Where(b => Matches(b, filter)).GroupBy(b => b.RemoteName ?? "remote", StringComparer.Ordinal))
        {
            var branches = group.ToList();
            rows.Add(new BranchGroupHeader(group.Key, branches.Count, isRemote: true));
            rows.AddRange(branches);
        }

        var selected = SelectedBranch;
        Rows.SyncWith(rows);
        HasNoMatches = HasLoaded && rows.Count == 0 && (_local.Count + _remote.Count) > 0;
        if (selected is not null && !rows.Contains(selected))
        {
            SelectedBranch = rows.OfType<BranchRowViewModel>().FirstOrDefault();
        }
    }

    private static bool Matches(BranchRowViewModel branch, string filter) =>
        filter.Length == 0 || branch.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                           || branch.TipSubject?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<T> OptionalAsync<T>(Func<Task<T>> load, T fallback)
    {
        try
        {
            return await load().ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            // Decorations (merged badges, default branch) never block the list.
            return fallback;
        }
    }
}
