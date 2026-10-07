using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.WorkItems;

namespace ForgeDesk.Presentation.Tasks;

/// <summary>A column of the board: its cards (filtered), its count and its inline quick-add box.</summary>
public sealed partial class TaskColumnViewModel : ObservableObject
{
    private readonly TasksSectionViewModel _owner;

    internal TaskColumnViewModel(TasksSectionViewModel owner, WorkItemStatus status)
    {
        _owner = owner;
        Status = status;
    }

    public WorkItemStatus Status { get; }

    public string Title => WorkItemText.StatusName(Status);

    public bool IsDoneColumn => Status == WorkItemStatus.Done;

    /// <summary>UI automation id ("Tasks.Column.InProgress").</summary>
    public string AutomationId => $"Tasks.Column.{Status}";

    public string QuickAddAutomationId => $"Tasks.QuickAdd.{Status}";

    public string AddButtonAutomationId => $"Tasks.AddButton.{Status}";

    /// <summary>The cards shown, in board order.</summary>
    public ObservableCollection<TaskCardViewModel> Cards { get; } = [];

    /// <summary>Cards of the column matching the filters (shown or collapsed).</summary>
    [ObservableProperty]
    public partial int Count { get; internal set; }

    /// <summary>Done cards older than two weeks, collapsed behind "Show n more".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden), nameof(ShowMoreText))]
    public partial int HiddenCount { get; internal set; }

    public bool HasHidden => HiddenCount > 0;

    public string ShowMoreText => $"Show {HiddenCount} more";

    [ObservableProperty]
    public partial bool IsVisible { get; internal set; } = true;

    [ObservableProperty]
    public partial bool IsQuickAddOpen { get; set; }

    [ObservableProperty]
    public partial string QuickAddText { get; set; } = string.Empty;

    /// <summary>Incremented to ask the view to focus the quick-add box (even when already open).</summary>
    [ObservableProperty]
    public partial int QuickAddFocusRequest { get; private set; }

    [ObservableProperty]
    public partial bool IsAdding { get; private set; }

    public string AddToolTip => $"Add a task to {Title}";

    /// <summary>"+" in the header: opens the quick-add box and focuses it.</summary>
    [RelayCommand]
    public void OpenQuickAdd()
    {
        IsQuickAddOpen = true;
        QuickAddFocusRequest++;
    }

    /// <summary>Esc: closes the box and forgets the text.</summary>
    [RelayCommand]
    private void CancelQuickAdd()
    {
        IsQuickAddOpen = false;
        QuickAddText = string.Empty;
    }

    /// <summary>Enter: creates the task and keeps the box open for the next one.</summary>
    [RelayCommand]
    private async Task SubmitQuickAddAsync()
    {
        var title = QuickAddText.Trim();
        if (title.Length == 0 || IsAdding)
        {
            if (title.Length == 0)
            {
                CancelQuickAdd();
            }

            return;
        }

        IsAdding = true;
        try
        {
            if (await _owner.CreateAsync(Status, title).ConfigureAwait(true))
            {
                QuickAddText = string.Empty;
                QuickAddFocusRequest++;
            }
        }
        finally
        {
            IsAdding = false;
        }
    }

    [RelayCommand]
    private void ShowHidden() => _owner.ShowOlderDone = true;
}
