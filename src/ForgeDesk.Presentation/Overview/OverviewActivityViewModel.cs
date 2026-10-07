using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Activity;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Overview;

/// <summary>"Recent activity" card: the last entries of the project's journal, updated live.</summary>
public sealed partial class OverviewActivityViewModel : OverviewCardViewModel
{
    internal const int Count = 8;

    private readonly IActivityLog _log;
    private readonly IShellIntegration _shell;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeZoneInfo _timeZone;

    public OverviewActivityViewModel(ProjectContext context, IActivityLog log, IShellIntegration shell, INotificationService notifications,
        IUiDispatcher dispatcher, TimeZoneInfo? timeZone = null)
        : base(context)
    {
        _log = log;
        _shell = shell;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _log.EntryAdded += OnEntryAdded;
    }

    public ObservableCollection<ActivityItemViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    protected override string ErrorTitle => "Could not load the activity";

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var entries = await _log.QueryAsync(new ActivityQuery { ProjectId = Context.ProjectId, Limit = Count }, cancellationToken).ConfigureAwait(true);
        Entries.Clear();
        foreach (var entry in entries.Take(Count))
        {
            Entries.Add(Create(entry));
        }

        IsEmpty = Entries.Count == 0;
    }

    [RelayCommand]
    private void Open(ActivityItemViewModel? item)
    {
        if (item?.Target is not { } target)
        {
            return;
        }

        try
        {
            if (target.Kind == ActivityTargetKind.Url && target.Url is { } url)
            {
                _shell.OpenUrl(url);
            }
            else if (target.Section is { } section)
            {
                Context.RequestNavigation(section, target.Argument);
            }
        }
        catch (Exception ex)
        {
            _notifications.ShowError(ErrorInfo.From(ex, "Could not open this entry"));
        }
    }

    [RelayCommand]
    private void ViewAll() => Context.RequestNavigation(WorkspaceSection.Activity);

    private ActivityItemViewModel Create(ActivityEntry entry) => new(entry, ActivityTarget.Resolve(entry, isGlobal: false), _timeZone);

    private void OnEntryAdded(object? sender, ActivityEntry entry)
    {
        if (!string.Equals(entry.ProjectId, Context.ProjectId, StringComparison.Ordinal))
        {
            return;
        }

        _dispatcher.Post(() => Insert(entry));
    }

    internal void Insert(ActivityEntry entry)
    {
        if (IsDisposed || !HasLoaded || Entries.Any(e => e.Id == entry.Id))
        {
            return;
        }

        var index = 0;
        while (index < Entries.Count && (Entries[index].At > entry.At || (Entries[index].At == entry.At && Entries[index].Id > entry.Id)))
        {
            index++;
        }

        if (index >= Count)
        {
            return;
        }

        Entries.Insert(index, Create(entry));
        while (Entries.Count > Count)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }

        IsEmpty = false;
    }

    protected override void OnDispose() => _log.EntryAdded -= OnEntryAdded;
}
