using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Dashboard;

/// <summary>Order of the dashboard (persisted in <c>AppSettings.DashboardSort</c>).</summary>
public enum DashboardSortMode
{
    /// <summary>Projects that need attention first, most severe first.</summary>
    Attention,

    /// <summary>Most recently opened first.</summary>
    Recent,

    /// <summary>Alphabetical.</summary>
    Name,
}

/// <summary>Cards or rows (persisted in <c>AppSettings.DashboardLayout</c>).</summary>
public enum DashboardLayout
{
    Grid,
    List,
}

public enum DashboardFilterKind
{
    All,
    Attention,
    Pinned,
    Running,
    Group,
}

/// <summary>Conversions between the dashboard preferences and their persisted text values.</summary>
public static class DashboardPreferences
{
    public static DashboardSortMode ParseSort(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "RECENT" => DashboardSortMode.Recent,
        "NAME" => DashboardSortMode.Name,
        _ => DashboardSortMode.Attention,
    };

    public static string ToSetting(DashboardSortMode mode) => mode switch
    {
        DashboardSortMode.Recent => "recent",
        DashboardSortMode.Name => "name",
        _ => "attention",
    };

    public static DashboardLayout ParseLayout(string? value) =>
        string.Equals(value?.Trim(), "list", StringComparison.OrdinalIgnoreCase) ? DashboardLayout.List : DashboardLayout.Grid;

    public static string ToSetting(DashboardLayout layout) => layout == DashboardLayout.List ? "list" : "grid";
}

/// <summary>An entry of the sort drop-down.</summary>
public sealed record DashboardSortOption(DashboardSortMode Mode, string Title, string Description)
{
    public static IReadOnlyList<DashboardSortOption> All { get; } =
    [
        new(DashboardSortMode.Attention, "Attention", "Projects that need you first"),
        new(DashboardSortMode.Recent, "Recently opened", "The projects you opened last first"),
        new(DashboardSortMode.Name, "Name", "Alphabetical order"),
    ];

    public static DashboardSortOption For(DashboardSortMode mode) => All.First(o => o.Mode == mode);

    public override string ToString() => Title;
}

/// <summary>A filter chip above the projects ("Needs attention 3", "Work 5").</summary>
public sealed partial class DashboardFilterViewModel : ObservableObject
{
    public DashboardFilterViewModel(DashboardFilterKind kind, string title, string icon, string? group = null)
    {
        Kind = kind;
        Title = title;
        Icon = icon;
        Group = group;
    }

    public DashboardFilterKind Kind { get; }

    public string Title { get; }

    /// <summary>WPF-UI SymbolRegular name.</summary>
    public string Icon { get; }

    /// <summary>The group name for <see cref="DashboardFilterKind.Group"/> chips.</summary>
    public string? Group { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(AutomationName))]
    public partial int Count { get; set; }

    /// <summary>The chip is the selected filter (it then stays visible even when empty).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool IsActive { get; set; }

    /// <summary>Optional chips (pinned, running) hide themselves while empty, unless selected.</summary>
    public bool IsVisible => Kind is DashboardFilterKind.All or DashboardFilterKind.Attention || Count > 0 || IsActive;

    public string AutomationName => $"{Title}, {Format.Count(Count, "project")}";

    public bool Matches(ProjectCardViewModel card) => Kind switch
    {
        DashboardFilterKind.Attention => card.NeedsAttention,
        DashboardFilterKind.Pinned => card.IsPinned,
        DashboardFilterKind.Running => card.IsRunning,
        DashboardFilterKind.Group => string.Equals(card.Group, Group, StringComparison.CurrentCultureIgnoreCase),
        _ => true,
    };
}

/// <summary>A reason a project needs attention, as shown on its card.</summary>
public sealed record AttentionItemViewModel(AttentionLevel Level, string Message, WorkspaceSection? Section)
{
    public StatusTone Tone => Level switch
    {
        AttentionLevel.Critical => StatusTone.Danger,
        AttentionLevel.Warning => StatusTone.Warning,
        AttentionLevel.Info => StatusTone.Info,
        _ => StatusTone.Neutral,
    };

    /// <summary>The attention reason names a workspace tab that can be opened directly.</summary>
    public bool CanOpen => Section is not null;

    public string ToolTip => Section is { } section ? $"{Message}\nOpen the {WorkspaceSectionInfo.For(section).Title} tab" : Message;

    public static AttentionItemViewModel From(AttentionReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        WorkspaceSection? section = Enum.TryParse<WorkspaceSection>(reason.Section, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;
        return new AttentionItemViewModel(reason.Level, reason.Message, section);
    }
}
