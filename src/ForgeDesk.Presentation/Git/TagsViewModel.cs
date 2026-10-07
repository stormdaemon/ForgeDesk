using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

/// <summary>A tag: name, annotated or lightweight, date, message and the commit it points at.</summary>
public sealed class TagRowViewModel
{
    public TagRowViewModel(GitTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        Tag = tag;
    }

    public GitTag Tag { get; }

    public string Name => Tag.Name;

    public bool IsAnnotated => Tag.IsAnnotated;

    public string KindText => IsAnnotated ? "Annotated" : "Lightweight";

    public DateTimeOffset? Date => Tag.Date;

    /// <summary>First line of the tag message (annotated tags).</summary>
    public string? Summary => Tag.Message is { Length: > 0 } message ? message.ReplaceLineEndings("\n").Split('\n')[0].Trim() : null;

    public string TargetSha => Tag.TargetSha;

    public string ShortSha => TargetSha.Length > 7 ? TargetSha[..7] : TargetSha;

    public string ToolTip
    {
        get
        {
            var kind = IsAnnotated ? "Annotated tag" : "Lightweight tag";
            var text = $"{Name} · {kind} on {ShortSha}";
            return Tag.Message is { Length: > 0 } message ? $"{text}\n\n{message}" : text;
        }
    }
}

/// <summary>The Tags view: every tag with its target, creating, pushing and deleting tags.</summary>
public sealed partial class TagsViewModel : GitSubViewModel
{
    private IReadOnlyList<TagRowViewModel> _all = [];
    private string? _selectAfterLoad;

    public TagsViewModel(GitSectionContext section)
        : base(section)
    {
    }

    public override GitView View => GitView.Tags;

    public ObservableCollection<TagRowViewModel> Tags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand), nameof(DeleteCommand))]
    public partial TagRowViewModel? SelectedTag { get; set; }

    public bool HasSelection => SelectedTag is not null;

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasTags { get; private set; }

    [ObservableProperty]
    public partial bool HasNoMatches { get; private set; }

    public bool IsEmpty => HasLoaded && !IsLoading && !HasError && !HasTags;

    /// <summary>Tags need a commit to point at.</summary>
    public bool CanCreate => Context.GitStatus is { IsUnborn: false };

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

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public override async Task LoadAsync()
    {
        var selected = SelectedTag?.Name;
        IsLoading = Tags.Count == 0;
        try
        {
            await RunAsync(async () =>
            {
                var tags = await Git.GetTagsAsync(Root, Section.Lifetime).ConfigureAwait(true);
                var previous = _all.ToDictionary(t => t.Tag);
                _all = tags.Select(t => previous.TryGetValue(t, out var row) ? row : new TagRowViewModel(t)).ToList();
                HasTags = _all.Count > 0;
                ApplyFilter();
                var wanted = _selectAfterLoad ?? selected;
                _selectAfterLoad = null;
                SelectedTag = (wanted is null ? null : Tags.FirstOrDefault(t => t.Name == wanted)) ?? Tags.FirstOrDefault();
            }, errorTitle: "Could not list the tags").ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanCreate));
            CreateCommand.NotifyCanExecuteChanged();
        }
    }

    internal override Task NavigateAsync(GitNavigation navigation)
    {
        if (navigation.Name is { } name)
        {
            if (Tags.All(t => t.Name != name) && FilterText.Length > 0)
            {
                FilterText = string.Empty;
            }

            if (Tags.FirstOrDefault(t => t.Name == name) is { } tag)
            {
                SelectedTag = tag;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Tags HEAD (from the toolbar); History tags any commit.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var status = Context.GitStatus;
        var head = status?.HeadSha is { Length: > 0 } sha ? sha[..Math.Min(7, sha.Length)] : "HEAD";
        var description = status?.Branch is { } branch ? $"HEAD ({branch} · {head})" : $"HEAD ({head})";
        var created = await GitFlows.CreateTagAsync(Section, null, description, _all.Select(t => t.Name).ToArray()).ConfigureAwait(true);
        if (created is null)
        {
            return;
        }

        // The repository change reloads the list; select the new tag now or when that load lands.
        FilterText = string.Empty;
        if (Tags.FirstOrDefault(t => t.Name == created) is { } tag)
        {
            SelectedTag = tag;
        }
        else
        {
            _selectAfterLoad = created;
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnTag))]
    private async Task PushAsync(TagRowViewModel? row)
    {
        row ??= SelectedTag;
        if (row is null)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await GitFlows.PushTagAsync(Section, row.Name).ConfigureAwait(true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Deletes the tag in this repository only (a pushed tag stays on the remote).</summary>
    [RelayCommand(CanExecute = nameof(CanActOnTag))]
    private async Task DeleteAsync(TagRowViewModel? row)
    {
        row ??= SelectedTag;
        if (row is null)
        {
            return;
        }

        bool confirmed;
        try
        {
            confirmed = await Section.Dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = $"Delete tag {row.Name}?",
                Message = $"The tag is deleted from this repository only. If it was pushed, it stays on the remote until you delete it there.",
                ConfirmText = "Delete tag",
                IsDestructive = true,
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                ShowError(ex, "Could not delete the tag");
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
                await Git.DeleteTagAsync(Root, row.Name, Section.Lifetime).ConfigureAwait(true);
                await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
                Notify($"Deleted tag {row.Name}", "Deleted locally. A copy already pushed stays on the remote.");
            }, $"Could not delete {row.Name}").ConfigureAwait(true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanActOnTag(TagRowViewModel? row) => (row ?? SelectedTag) is not null;

    [RelayCommand]
    private Task ShowInHistoryAsync(TagRowViewModel? row)
    {
        var target = row ?? SelectedTag;
        return target is null ? Task.CompletedTask : Section.Navigate(GitNavigation.History(target.TargetSha));
    }

    [RelayCommand]
    private void CopyName(TagRowViewModel? row)
    {
        if ((row ?? SelectedTag) is { } target)
        {
            Copy(target.Name, "Tag name");
        }
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        var rows = _all.Where(t => filter.Length == 0
                                   || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || t.Summary?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true).ToList();
        var selected = SelectedTag;
        Tags.SyncWith(rows);
        HasNoMatches = rows.Count == 0 && _all.Count > 0;
        if (selected is not null && !rows.Contains(selected))
        {
            SelectedTag = rows.FirstOrDefault();
        }
    }
}
