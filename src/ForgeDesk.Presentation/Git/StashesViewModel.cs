using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

/// <summary>A stash entry: its message without git's "On main:" prefix, the branch it was made on, and when.</summary>
public sealed partial class StashRowViewModel
{
    public StashRowViewModel(GitStash stash)
    {
        ArgumentNullException.ThrowIfNull(stash);
        Stash = stash;
        var match = StashMessage().Match(stash.Message);
        if (match.Success)
        {
            Branch = match.Groups["branch"].Value;
            var text = match.Groups["message"].Value.Trim();
            Message = match.Groups["wip"].Success ? $"Work in progress · {text}" : text;
        }
        else
        {
            Message = stash.Message.Trim();
        }

        if (Message.Length == 0)
        {
            Message = "(no message)";
        }
    }

    public GitStash Stash { get; }

    public int Index => Stash.Index;

    /// <summary>"stash@{0}".</summary>
    public string Name => Stash.Name;

    public string Message { get; }

    /// <summary>The branch the changes were stashed from, when git recorded it.</summary>
    public string? Branch { get; }

    public DateTimeOffset? Date => Stash.Date;

    public string ToolTip => $"{Name}\n{Stash.Message}";

    [GeneratedRegex(@"^(?:(?<wip>WIP on)|On) (?<branch>[^:]+):\s?(?<message>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex StashMessage();
}

/// <summary>
/// The Stashes view: stashed changes with their content (files and diffs), restoring (pop) and
/// dropping them, and stashing the current changes.
/// </summary>
public sealed partial class StashesViewModel : GitSubViewModel
{
    public StashesViewModel(GitSectionContext section)
        : base(section)
    {
    }

    public override GitView View => GitView.Stashes;

    public ObservableCollection<StashRowViewModel> Stashes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(PopCommand), nameof(DropCommand))]
    public partial StashRowViewModel? SelectedStash { get; set; }

    public bool HasSelection => SelectedStash is not null;

    /// <summary>Files and diffs of the selected stash.</summary>
    [ObservableProperty]
    public partial CommitDetailsViewModel? Details { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasStashes { get; private set; }

    public bool IsEmpty => HasLoaded && !IsLoading && !HasError && !HasStashes;

    /// <summary>There are local changes to stash.</summary>
    public bool CanStash => Context.GitStatus is { IsClean: false, IsUnborn: false };

    /// <summary>Label of the empty state's action, null when there is nothing to stash.</summary>
    public string? StashActionText => CanStash ? "Stash changes…" : null;

    public string EmptyDescription => CanStash
        ? "Stash your uncommitted changes to set them aside, switch to other work, and restore them later."
        : "There's nothing stashed. When you have uncommitted changes, stash them here to set them aside.";

    [ObservableProperty]
    public partial bool IsWorking { get; private set; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsLoading) or nameof(HasError))
        {
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    partial void OnSelectedStashChanged(StashRowViewModel? value)
    {
        Details?.Dispose();
        if (value is null)
        {
            Details = null;
            return;
        }

        var details = new CommitDetailsViewModel(Section, value.Name);
        Details = details;
        _ = details.LoadAsync();
    }

    /// <summary>The working tree changed: stashing may have become possible (or not).</summary>
    internal void OnStatusChanged()
    {
        OnPropertyChanged(nameof(CanStash));
        OnPropertyChanged(nameof(StashActionText));
        OnPropertyChanged(nameof(EmptyDescription));
        StashChangesCommand.NotifyCanExecuteChanged();
    }

    public override async Task LoadAsync()
    {
        var selectedIndex = SelectedStash?.Index;
        IsLoading = Stashes.Count == 0;
        try
        {
            await RunAsync(async () =>
            {
                var stashes = await Git.GetStashesAsync(Root, Section.Lifetime).ConfigureAwait(true);
                var previous = Stashes.ToDictionary(s => s.Stash);
                var rows = stashes.Select(s => previous.TryGetValue(s, out var row) ? row : new StashRowViewModel(s)).ToList();
                Stashes.SyncWith(rows);
                HasStashes = rows.Count > 0;
                SelectedStash = rows.FirstOrDefault(r => ReferenceEquals(r, SelectedStash))
                                ?? rows.FirstOrDefault(r => r.Index == selectedIndex)
                                ?? rows.FirstOrDefault();
            }, errorTitle: "Could not list the stashes").ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
            OnStatusChanged();
        }
    }

    internal override async Task NavigateAsync(GitNavigation navigation)
    {
        if (navigation.StartStash && StashChangesCommand.CanExecute(null))
        {
            await StashChangesCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStash))]
    private async Task StashChangesAsync()
    {
        var status = Context.GitStatus;
        if (status is null || status.IsClean)
        {
            Notify("Nothing to stash", "There are no uncommitted changes.", NotificationSeverity.Info);
            return;
        }

        var dialog = new StashDialogViewModel(status.Entries.Count, status.Entries.Count(e => e.IsUntracked), status.Branch);
        try
        {
            if (await Section.Dialogs.ShowDialogAsync(dialog).ConfigureAwait(true) != true)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                ShowError(ex, "Could not stash the changes");
            }

            return;
        }

        var message = string.IsNullOrWhiteSpace(dialog.Message) ? null : dialog.Message.Trim();
        IsWorking = true;
        try
        {
            await RunActionAsync(async () =>
            {
                await Git.StashAsync(Root, message, dialog.IncludeUntracked, Section.Lifetime).ConfigureAwait(true);
                await Section.Activity.SucceededAsync(ActivityKind.GitStash, $"Stashed {Format.Count(dialog.ChangedFiles, "change")}", message, status.Branch)
                    .ConfigureAwait(true);
                // The section reloads this list when the repository or the stash count changes.
                await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
                Notify("Changes stashed", message ?? "Restore them from this list when you need them.");
            }, "Could not stash the changes").ConfigureAwait(true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Applies the stash to the working tree and removes it; on conflicts git keeps it.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnStash))]
    private async Task PopAsync(StashRowViewModel? row)
    {
        row ??= SelectedStash;
        if (row is null)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await Git.StashPopAsync(Root, row.Index, Section.Lifetime).ConfigureAwait(true);
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.SucceededAsync(ActivityKind.GitStash, $"Restored stash: {row.Message}", null, Context.GitStatus?.Branch).ConfigureAwait(true);
            Notify("Stash restored", $"{row.Message} is back in your working tree.");
        }
        catch (ForgeException ex) when (ex.Kind == ErrorKind.MergeConflict)
        {
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await Section.Activity.WarningAsync(ActivityKind.GitStash, $"Restored stash with conflicts: {row.Message}", ex.Message).ConfigureAwait(true);
            Notify("The stash was applied with conflicts",
                "Resolve the conflicted files in Changes. The stash was kept, so nothing is lost; drop it once you are done.", NotificationSeverity.Warning);
            await Section.Navigate(GitNavigation.Changes()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed.
        }
        catch (Exception ex)
        {
            ShowError(ex, "Could not restore the stash");
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnStash))]
    private async Task DropAsync(StashRowViewModel? row)
    {
        row ??= SelectedStash;
        if (row is null)
        {
            return;
        }

        bool confirmed;
        try
        {
            confirmed = await Section.Dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = "Drop this stash?",
                Message = $"\"{row.Message}\" and the changes it holds will be deleted. This can't be undone.",
                ConfirmText = "Drop stash",
                IsDestructive = true,
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                ShowError(ex, "Could not drop the stash");
            }

            return;
        }

        if (!confirmed)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await RunActionAsync(async () =>
            {
                await Git.StashDropAsync(Root, row.Index, Section.Lifetime).ConfigureAwait(true);
                await Section.Activity.SucceededAsync(ActivityKind.GitStash, $"Dropped stash: {row.Message}").ConfigureAwait(true);
                await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
                Notify("Stash dropped", row.Message);
            }, "Could not drop the stash").ConfigureAwait(true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanActOnStash(StashRowViewModel? row) => (row ?? SelectedStash) is not null;

    protected override void OnDisposed() => Details?.Dispose();
}
