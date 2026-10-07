using ForgeDesk.Core.Activity;
using ForgeDesk.Presentation.Activity;
using ForgeDesk.Presentation.Infrastructure;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Activity;

/// <summary>A clock frozen at <see cref="Now"/> in UTC (so day grouping does not depend on the machine).</summary>
public sealed class FrozenTime : TimeProvider
{
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>An in-memory activity journal behind an <see cref="IActivityLog"/> substitute that honours queries.</summary>
public sealed class ActivityHarness
{
    public ActivityHarness()
    {
        Log.QueryAsync(Arg.Any<ActivityQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var query = call.ArgAt<ActivityQuery>(0);
            Queries.Add(query);
            if (QueryFailure is { } failure)
            {
                return Task.FromException<IReadOnlyList<ActivityEntry>>(failure);
            }

            IEnumerable<ActivityEntry> result = Entries.OrderByDescending(e => e.At).ThenByDescending(e => e.Id);
            if (query.ProjectId is { } project)
            {
                result = result.Where(e => e.ProjectId == project);
            }

            if (query.Kinds is { Count: > 0 } kinds)
            {
                result = result.Where(e => kinds.Contains(e.Kind));
            }

            if (query.Outcome is { } outcome)
            {
                result = result.Where(e => e.Outcome == outcome);
            }

            if (query.Search is { } search)
            {
                result = result.Where(e => e.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || e.Detail?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
            }

            if (query.Before is { } before)
            {
                result = result.Where(e => e.At < before || (e.At == before && query.BeforeId is { } id && e.Id < id));
            }

            return Task.FromResult<IReadOnlyList<ActivityEntry>>(result.Take(query.Limit).ToList());
        });
    }

    public IActivityLog Log { get; } = Substitute.For<IActivityLog>();

    public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();

    public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

    public IShellIntegration Shell { get; } = Substitute.For<IShellIntegration>();

    public List<ActivityEntry> Entries { get; } = [];

    public List<ActivityQuery> Queries { get; } = [];

    public Exception? QueryFailure { get; set; }

    /// <summary>Immediate search, a frozen UTC clock and the given page size.</summary>
    public static ActivityTimelineOptions Options(int pageSize = 100) => new()
    {
        PageSize = pageSize,
        SearchDelay = TimeSpan.Zero,
        Time = new FrozenTime(),
    };

    public static ActivityEntry Entry(long id, DateTimeOffset at, string title, ActivityKind kind = ActivityKind.GitCommit,
        ActivityOutcome outcome = ActivityOutcome.Success, string? projectId = "p1", string? refKind = null, string? refValue = null,
        string? detail = null) => new()
    {
        Id = id,
        At = at,
        Title = title,
        Kind = kind,
        Outcome = outcome,
        ProjectId = projectId,
        RefKind = refKind,
        RefValue = refValue,
        Detail = detail,
    };

    public void Raise(ActivityEntry entry) =>
        Log.EntryAdded += NSubstitute.Raise.Event<EventHandler<ActivityEntry>>(Log, entry);
}
