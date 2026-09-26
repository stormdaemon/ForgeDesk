using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Workspace;

/// <summary>
/// One tab of the project workspace. Its section view model is created on first display and
/// kept alive while the project is open.
/// </summary>
public sealed partial class WorkspaceTabViewModel : ObservableObject
{
    public WorkspaceTabViewModel(WorkspaceSectionInfo info, bool isAvailable)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        IsAvailable = isAvailable;
    }

    public WorkspaceSectionInfo Info { get; }

    public WorkspaceSection Section => Info.Section;

    public string Title => Info.Title;

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon => Info.Icon;

    /// <summary>False when no feature registered this section: the tab is hidden.</summary>
    public bool IsAvailable { get; }

    /// <summary>Keyboard shortcut selecting this tab ("Ctrl+2"), by position among available tabs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial string? Shortcut { get; set; }

    /// <summary>Short badge text ("3"), or null for no counter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadgeText), nameof(HasBadgeDot))]
    public partial string? BadgeText { get; set; }

    /// <summary>Badge color; with no <see cref="BadgeText"/> a non-None kind renders as a dot (CI state).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadgeDot))]
    public partial StatusTone BadgeKind { get; set; }

    /// <summary>Explains the badge ("3 changed files", "CI failing").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial string? BadgeDescription { get; set; }

    public bool HasBadgeText => !string.IsNullOrEmpty(BadgeText);

    public bool HasBadgeDot => string.IsNullOrEmpty(BadgeText) && BadgeKind != StatusTone.None;

    public string ToolTip
    {
        get
        {
            var title = Shortcut is null ? Title : $"{Title} ({Shortcut})";
            return BadgeDescription is null ? title : $"{title} — {BadgeDescription}";
        }
    }

    /// <summary>The tab whose section is on screen.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; internal set; }

    /// <summary>The section view model, once created.</summary>
    public IWorkspaceSectionViewModel? ViewModel { get; internal set; }

    internal void SetBadge(string? text, StatusTone kind, string? description)
    {
        BadgeText = text;
        BadgeKind = kind;
        BadgeDescription = description;
    }

    internal void ClearBadge() => SetBadge(null, StatusTone.None, null);
}
