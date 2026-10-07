using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ForgeDesk.Core.Activity;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Activity;

/// <summary>A row of the activity timeline: a day header or an entry.</summary>
public abstract class ActivityRowViewModel : ObservableObject
{
    public abstract bool IsHeader { get; }
}

/// <summary>"Today", "Yesterday", "Monday", "12 September 2026".</summary>
public sealed partial class ActivityDayHeaderViewModel : ActivityRowViewModel
{
    public ActivityDayHeaderViewModel(DateOnly day) => Day = day;

    public override bool IsHeader => true;

    public DateOnly Day { get; }

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Count { get; set; }

    /// <summary>Label of a day relative to <paramref name="today"/> (both local dates).</summary>
    public static string LabelFor(DateOnly day, DateOnly today)
    {
        var culture = CultureInfo.CurrentCulture;
        var days = today.DayNumber - day.DayNumber;
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => culture.DateTimeFormat.GetDayName(day.DayOfWeek),
            _ when day.Year == today.Year => day.ToString("dddd d MMMM", culture),
            _ => day.ToString("d MMMM yyyy", culture),
        };
    }
}

/// <summary>One journal entry: icon by kind, tone by outcome, title, detail, time and where it leads.</summary>
public sealed partial class ActivityItemViewModel : ActivityRowViewModel
{
    public ActivityItemViewModel(ActivityEntry entry, ActivityTarget? target, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(timeZone);
        Entry = entry;
        Target = target;
        LocalTime = TimeZoneInfo.ConvertTime(entry.At, timeZone);
        Group = ActivityGroups.Of(entry.Kind);
    }

    public override bool IsHeader => false;

    public ActivityEntry Entry { get; }

    public long Id => Entry.Id;

    public DateTimeOffset At => Entry.At;

    /// <summary><see cref="At"/> in the user's time zone (used for day grouping).</summary>
    public DateTimeOffset LocalTime { get; }

    public DateOnly Day => DateOnly.FromDateTime(LocalTime.DateTime);

    public string Title => Entry.Title;

    public string? Detail => string.IsNullOrWhiteSpace(Entry.Detail) ? null : Entry.Detail;

    public bool HasDetail => Detail is not null;

    public ActivityGroup Group { get; }

    public string GroupTitle => ActivityGroups.Title(Group);

    /// <summary>WPF-UI SymbolRegular name for the kind of entry.</summary>
    public string Icon => ActivityGroups.Icon(Group);

    public ActivityOutcome Outcome => Entry.Outcome;

    public StatusTone Tone => ActivityGroups.ToneOf(Entry.Outcome);

    public bool IsFailure => Entry.Outcome == ActivityOutcome.Failure;

    public bool IsWarning => Entry.Outcome == ActivityOutcome.Warning;

    /// <summary>"14:32" (local).</summary>
    public string TimeText => LocalTime.ToString("t", CultureInfo.CurrentCulture);

    public string FullTimeText => LocalTime.ToString("f", CultureInfo.CurrentCulture);

    public string? ProjectId => Entry.ProjectId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    public partial string? ProjectName { get; set; }

    [ObservableProperty]
    public partial string? ProjectColor { get; set; }

    /// <summary>Shown on the global page only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    public partial bool ShowProject { get; set; }

    public bool HasProject => ShowProject && !string.IsNullOrEmpty(ProjectName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFollow))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    public partial ActivityTarget? Target { get; set; }

    public bool CanFollow => Target is not null;

    public string? ActionText => Target?.ActionText;

    public string ToolTip
    {
        get
        {
            var text = $"{GroupTitle} · {Outcome} · {FullTimeText}";
            return ActionText is { } action ? $"{text}\n{action} (Enter or double-click)" : text;
        }
    }

    /// <summary>Text copied by "Copy details".</summary>
    public string CopyText => Detail is null ? $"{FullTimeText}  {Title}" : $"{FullTimeText}  {Title}\n{Detail}";
}

/// <summary>A filter chip (All · Git · Commands · …).</summary>
public sealed class ActivityFilterViewModel(ActivityGroup group)
{
    public ActivityGroup Group { get; } = group;

    public string Title => ActivityGroups.Title(Group);

    public string Icon => ActivityGroups.Icon(Group);

    public string AutomationName => $"Show {Title.ToLowerInvariant()} activity";
}

/// <summary>Name and avatar color of a project, for entries on the global page.</summary>
public sealed record ActivityProjectInfo(string Name, string? Color);
