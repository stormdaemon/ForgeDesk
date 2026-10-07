using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Commands;

/// <summary>
/// The Commands tab: the project's build / test / dev scripts (detected and user-defined) grouped
/// by category, and its runs — live ones on top, then the history — with the selected run's
/// output, progress and actions.
/// </summary>
public sealed partial class CommandsSectionViewModel : ViewModelBase, IWorkspaceSectionViewModel, INavigationTarget, IRefreshable, IDisposable
{
    private readonly ICustomCommandStore _customCommands;
    private readonly ILogger _logger;
    private readonly Dictionary<string, CommandRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<CommandCategory, CommandGroupHeader> _headers = [];
    private readonly Dictionary<string, RunItemViewModel> _runs = new(StringComparer.Ordinal);
    private readonly RunGroupHeader _runningHeader = new("Running");
    private readonly RunGroupHeader _historyHeader = new("History");
    private IReadOnlyList<DetectedCommand> _custom = [];
    private ITimer? _ticker;
    private readonly HashSet<IRunSession> _subscribed = [];
    private Task? _loading;
    private bool _disposed;

    public CommandsSectionViewModel(ProjectContext context, WorkspaceServices services, ICustomCommandStore? customCommands = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        Context = context;
        Services = services;
        _customCommands = customCommands ?? NoCustomCommandStore.Instance;
        _logger = services.LoggerFactory.CreateLogger<CommandsSectionViewModel>();

        Context.PropertyChanged += OnContextPropertyChanged;
        services.Runs.RunStarted += OnRunStarted;
        services.Runs.RunCompleted += OnRunCompleted;
        RebuildCommands();
    }

    public WorkspaceSection Section => WorkspaceSection.Commands;

    public ProjectContext Context { get; }

    internal WorkspaceServices Services { get; }

    internal DateTimeOffset Now => Services.Time.GetLocalNow();

    /// <summary>How often live output is appended (Timeout.InfiniteTimeSpan: only on explicit flushes, for tests).</summary>
    internal TimeSpan LogBatchInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Tick of running durations and the stalled check (Timeout.InfiniteTimeSpan disables the timer, for tests).</summary>
    internal TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    public bool IsActive { get; private set; }

    public bool HasLoaded { get; private set; }

    /// <summary>The first load is in progress (skeletons).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoCommands), nameof(ShowNoRuns))]
    public partial bool IsLoading { get; private set; }

    // ----- Commands ---------------------------------------------------------------------

    /// <summary>Category headers and commands, filtered by the search and the category filter.</summary>
    public ObservableCollection<CommandListEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial CommandListEntry? SelectedEntry { get; set; }

    public CommandRowViewModel? SelectedCommand => SelectedEntry as CommandRowViewModel;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Only commands of this category are listed (set by deep links such as "Build").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCategoryFilter), nameof(CategoryFilterText))]
    public partial CommandCategory? CategoryFilter { get; set; }

    public bool HasCategoryFilter => CategoryFilter is not null;

    public string CategoryFilterText => CategoryFilter is { } category ? $"Category: {CommandCategories.Name(category)}" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommands), nameof(ShowNoCommands), nameof(ShowNoMatches), nameof(CommandCountText))]
    public partial int CommandCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoMatches))]
    public partial int VisibleCommandCount { get; private set; }

    public bool HasCommands => CommandCount > 0;

    public string CommandCountText => Format.Count(CommandCount, "command");

    /// <summary>Nothing detected and no custom command: explain what ForgeDesk looks for.</summary>
    public bool ShowNoCommands => HasLoaded && !IsLoading && CommandCount == 0;

    public bool ShowNoMatches => CommandCount > 0 && VisibleCommandCount == 0;

    /// <summary>The profile is being detected again.</summary>
    [ObservableProperty]
    public partial bool IsDetecting { get; private set; }

    public string NoCommandsDescription =>
        $"ForgeDesk looks for package.json scripts, .sln and .csproj files, Cargo.toml, go.mod, pyproject.toml, Makefile, Gradle and Maven builds, "
        + $"and other well-known project files. None were found in {Context.Project.Name}. Add your own command, or detect again after adding one.";

    // ----- Runs -------------------------------------------------------------------------

    /// <summary>Running commands first, then the history (newest first), with group headers.</summary>
    public ObservableCollection<RunListEntry> Runs { get; } = [];

    /// <summary>The selected entry of the Runs list (two-way; headers are ignored).</summary>
    [ObservableProperty]
    public partial RunListEntry? SelectedRunEntry { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRun))]
    public partial RunItemViewModel? SelectedRun { get; private set; }

    public bool HasSelectedRun => SelectedRun is not null;

    /// <summary>The selected run's header, actions and output.</summary>
    [ObservableProperty]
    public partial RunDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuns), nameof(ShowNoRuns))]
    public partial int RunCount { get; private set; }

    [ObservableProperty]
    public partial int RunningCount { get; private set; }

    public bool HasRuns => RunCount > 0;

    public bool ShowNoRuns => HasLoaded && !IsLoading && RunCount == 0;

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsActive = true;
        if (!HasLoaded)
        {
            await EnsureLoadedAsync().ConfigureAwait(true);
        }
        else
        {
            TickAll();
        }

        // A command started from the header or the palette while the tab was hidden: show it.
        SelectNewestLiveRunIfIdle();
        UpdateTicker();
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdateTicker();
    }

    /// <summary>
    /// Deep link: a run id selects that run (loading it from history when needed); a
    /// <see cref="CommandCategory"/> (or its name) filters the commands to that category.
    /// </summary>
    public async Task NavigateToAsync(object argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        await EnsureLoadedAsync().ConfigureAwait(true);
        switch (argument)
        {
            case CommandCategory category:
                ShowCategory(category);
                break;
            case string text when _runs.ContainsKey(text):
                SelectRun(_runs[text]);
                break;
            case string text when CommandCategories.TryParse(text) is { } category:
                ShowCategory(category);
                break;
            case string runId when !string.IsNullOrWhiteSpace(runId):
                await SelectRunFromHistoryAsync(runId).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>Starts a run of <paramref name="request"/> and selects it. Failures are reported as notifications.</summary>
    public async Task<IRunSession?> StartAsync(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        IRunSession? session = null;
        await RunAsync(async () =>
        {
            session = await Services.Runs.StartAsync(request, Context.Lifetime).ConfigureAwait(true);
            var item = EnsureRun(session);
            SelectRun(item);
        }, errorTitle: $"Could not start {request.Label}", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
        return session;
    }

    /// <summary>The run request of a command, run from its working directory inside the project.</summary>
    public static RunRequest CreateRequest(ProjectContext context, DetectedCommand command)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        var directory = string.IsNullOrEmpty(command.WorkingDirectory) ? context.Root : PathUtil.ResolveUnder(context.Root, command.WorkingDirectory);
        return new RunRequest
        {
            ProjectId = context.ProjectId,
            Label = command.Name,
            CommandLine = command.CommandLine,
            WorkingDirectory = directory,
            Category = command.Category,
            CommandId = command.Id,
        };
    }

    // ----- Commands: actions ------------------------------------------------------------

    /// <summary>Run (▶) or Stop (■) of a command row.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunOrStopAsync(CommandRowViewModel? row)
    {
        row ??= SelectedCommand;
        if (row is null || row.IsStarting)
        {
            return;
        }

        if (row.RunningRunId is { } runId)
        {
            await RunAsync(() => Services.Runs.CancelAsync(runId), errorTitle: $"Could not stop {row.Name}", errorMode: ErrorMode.Toast,
                notifications: Services.Notifications).ConfigureAwait(true);
            return;
        }

        row.IsStarting = true;
        try
        {
            RunRequest request;
            try
            {
                request = CreateRequest(Context, row.Command);
            }
            catch (Exception ex) when (!ex.IsCancellation())
            {
                Services.Notifications.ShowError(ErrorInfo.From(ex, $"Could not start {row.Name}"));
                return;
            }

            await StartAsync(request).ConfigureAwait(true);
        }
        finally
        {
            row.IsStarting = false;
        }
    }

    [RelayCommand]
    private async Task AddCommandAsync(CommandCategory? category)
    {
        var dialog = new CommandEditorDialogViewModel(Context.Root, category: category ?? CategoryFilter);
        if (await ShowEditorAsync(dialog).ConfigureAwait(true) != true)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var added = await _customCommands.AddAsync(Context.ProjectId, dialog.ResultName, dialog.ResultCommandLine, dialog.ResultCategory,
                dialog.ResultWorkingDirectory, Context.Lifetime).ConfigureAwait(true);
            await ReloadCustomCommandsAsync().ConfigureAwait(true);
            if (_rows.TryGetValue(added.Id, out var row))
            {
                if (!Entries.Contains(row))
                {
                    SearchText = string.Empty;
                    CategoryFilter = null;
                }

                SelectedEntry = row;
            }

            Services.Notifications.Show($"Added {added.Name}", added.CommandLine, NotificationSeverity.Success);
        }, errorTitle: "Could not add the command", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task EditCommandAsync(CommandRowViewModel? row)
    {
        row ??= SelectedCommand;
        if (row is not { IsCustom: true })
        {
            return;
        }

        var dialog = new CommandEditorDialogViewModel(Context.Root, row.Command);
        if (await ShowEditorAsync(dialog).ConfigureAwait(true) != true)
        {
            return;
        }

        var updated = row.Command with
        {
            Name = dialog.ResultName,
            CommandLine = dialog.ResultCommandLine,
            Category = dialog.ResultCategory,
            WorkingDirectory = dialog.ResultWorkingDirectory ?? string.Empty,
        };

        await RunAsync(async () =>
        {
            await _customCommands.UpdateAsync(Context.ProjectId, updated, Context.Lifetime).ConfigureAwait(true);
            await ReloadCustomCommandsAsync().ConfigureAwait(true);
        }, errorTitle: "Could not save the command", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteCommandAsync(CommandRowViewModel? row)
    {
        row ??= SelectedCommand;
        if (row is not { IsCustom: true })
        {
            return;
        }

        bool confirmed;
        try
        {
            confirmed = await Services.Dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = $"Delete {row.Name}?",
                Message = $"The command \"{row.CommandLine}\" is removed from {Context.Project.Name}. Its past runs stay in the history.",
                ConfirmText = "Delete command",
                IsDestructive = true,
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not delete the command"));
            return;
        }

        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _customCommands.DeleteAsync(Context.ProjectId, row.Id, Context.Lifetime).ConfigureAwait(true);
            await ReloadCustomCommandsAsync().ConfigureAwait(true);
            Services.Notifications.Show($"Deleted {row.Name}", null, NotificationSeverity.Success);
        }, errorTitle: "Could not delete the command", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CopyCommandLine(CommandRowViewModel? row)
    {
        row ??= SelectedCommand;
        if (row is null)
        {
            return;
        }

        try
        {
            Services.Shell.CopyToClipboard(row.CommandLine);
            Services.Notifications.Show("Command copied", row.CommandLine, NotificationSeverity.Info);
        }
        catch (Exception ex)
        {
            Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not copy to the clipboard"));
        }
    }

    /// <summary>Shows the runs of a command: filters nothing, selects its latest run.</summary>
    [RelayCommand]
    private void ShowLatestRun(CommandRowViewModel? row)
    {
        row ??= SelectedCommand;
        if (row is null)
        {
            return;
        }

        var latest = _runs.Values.Where(r => string.Equals(r.CommandId, row.Id, StringComparison.Ordinal)).MaxBy(r => r.StartedAt);
        if (latest is not null)
        {
            SelectRun(latest);
        }
    }

    /// <summary>Detects the project's commands again (after adding a script, a Makefile…).</summary>
    [RelayCommand]
    private async Task RedetectAsync()
    {
        if (IsDetecting)
        {
            return;
        }

        IsDetecting = true;
        try
        {
            await RunAsync(async () =>
            {
                await Context.RefreshProfileAsync().ConfigureAwait(true);
                await ReloadCustomCommandsAsync().ConfigureAwait(true);
                Services.Notifications.Show("Commands detected again", Format.Count(CommandCount, "command") + $" in {Context.Project.Name}",
                    NotificationSeverity.Info);
            }, errorTitle: "Could not detect the commands", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
        }
        finally
        {
            IsDetecting = false;
        }
    }

    [RelayCommand]
    private void ClearCategoryFilter() => CategoryFilter = null;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    // ----- Runs: actions ----------------------------------------------------------------

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CancelRunAsync(RunItemViewModel? run)
    {
        run ??= SelectedRun;
        return run is { IsRunning: true }
            ? RunAsync(() => Services.Runs.CancelAsync(run.Id), errorTitle: $"Could not stop {run.Label}", errorMode: ErrorMode.Toast,
                notifications: Services.Notifications)
            : Task.CompletedTask;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task RunAgainAsync(RunItemViewModel? run)
    {
        run ??= SelectedRun;
        return run is null ? Task.CompletedTask : StartAsync(run.Request);
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        bool confirmed;
        try
        {
            confirmed = await Services.Dialogs.ConfirmAsync(new ConfirmOptions
            {
                Title = "Clear the run history?",
                Message = $"The finished runs of {Context.Project.Name} and their logs are deleted. Running commands are not affected.",
                ConfirmText = "Clear history",
                IsDestructive = true,
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not clear the history"));
            return;
        }

        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Services.Runs.DeleteHistoryAsync(Context.ProjectId, Context.Lifetime).ConfigureAwait(true);
            foreach (var finished in _runs.Values.Where(r => !r.IsRunning).ToList())
            {
                _runs.Remove(finished.Id);
            }

            RebuildRuns();
            UpdateCommandStates();
        }, errorTitle: "Could not clear the history", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
    }

    /// <summary>F5: reloads custom commands and the run history.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!HasLoaded)
        {
            await EnsureLoadedAsync().ConfigureAwait(true);
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Retry of the error panel.</summary>
    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsActive = false;
        Context.PropertyChanged -= OnContextPropertyChanged;
        Services.Runs.RunStarted -= OnRunStarted;
        Services.Runs.RunCompleted -= OnRunCompleted;
        _ticker?.Dispose();
        _ticker = null;
        foreach (var session in _subscribed.ToList())
        {
            Unsubscribe(session);
        }

        Detail?.Dispose();
    }

    private void Unsubscribe(IRunSession session)
    {
        if (_subscribed.Remove(session))
        {
            session.StatusChanged -= OnSessionStatusChanged;
            session.ProgressChanged -= OnSessionProgressChanged;
        }
    }

    // ----- Loading ----------------------------------------------------------------------

    private Task EnsureLoadedAsync() => HasLoaded ? Task.CompletedTask : _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = !HasLoaded;
        var succeeded = await RunAsync(async () =>
        {
            var customTask = _customCommands.GetAsync(Context.ProjectId, Context.Lifetime);
            var historyLimit = Math.Clamp(Services.Settings.Current.RunHistoryPerProject, 20, 500);
            var historyTask = Services.Runs.GetHistoryAsync(Context.ProjectId, historyLimit, Context.Lifetime);
            await Task.WhenAll(customTask, historyTask).ConfigureAwait(true);
            _custom = customTask.Result;
            ApplyHistory(historyTask.Result);
        }, "Loading commands…", "Could not load the commands").ConfigureAwait(true);

        _loading = null;
        IsLoading = false;
        if (succeeded)
        {
            HasLoaded = true;
            if (SelectedRun is null && Runs.OfType<RunItemViewModel>().FirstOrDefault() is { } latest)
            {
                SelectRun(latest);
            }
        }

        RebuildCommands();
        UpdateTicker();
        OnPropertyChanged(nameof(ShowNoCommands));
        OnPropertyChanged(nameof(ShowNoRuns));
    }

    private async Task ReloadCustomCommandsAsync()
    {
        _custom = await _customCommands.GetAsync(Context.ProjectId, Context.Lifetime).ConfigureAwait(true);
        RebuildCommands();
    }

    private void ApplyHistory(IReadOnlyList<RunRecord> history)
    {
        var now = Now;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in Services.Runs.ActiveRuns.Where(IsMine))
        {
            keep.Add(EnsureRun(session, rebuild: false).Id);
        }

        foreach (var record in history)
        {
            if (keep.Contains(record.Id))
            {
                continue;
            }

            keep.Add(record.Id);
            if (_runs.TryGetValue(record.Id, out var existing))
            {
                if (record.IsFinished)
                {
                    existing.Apply(record, now);
                }
            }
            else if (record.IsFinished)
            {
                _runs[record.Id] = new RunItemViewModel(record, now);
            }
            else
            {
                // Persisted as running, but no live session: it belongs to another instance, or was interrupted.
                _runs[record.Id] = new RunItemViewModel(record with { Status = RunStatus.Interrupted }, now);
            }
        }

        foreach (var stale in _runs.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            if (!ReferenceEquals(_runs[stale], SelectedRun))
            {
                _runs.Remove(stale);
            }
        }

        RebuildRuns();
        UpdateCommandStates();
    }

    private async Task SelectRunFromHistoryAsync(string runId)
    {
        await RunAsync(async () =>
        {
            if (Services.Runs.FindActive(runId) is { } session && IsMine(session))
            {
                SelectRun(EnsureRun(session));
                return;
            }

            var record = await Services.Runs.GetAsync(runId, Context.Lifetime).ConfigureAwait(true);
            if (record is null || !string.Equals(record.ProjectId, Context.ProjectId, StringComparison.Ordinal))
            {
                throw new ForgeException(ErrorKind.NotFound, "This run is no longer in the history.",
                    "Old runs are removed when the history reaches its limit (Settings › Runs).");
            }

            if (!_runs.TryGetValue(record.Id, out var item))
            {
                item = new RunItemViewModel(record, Now);
                _runs[record.Id] = item;
                RebuildRuns();
            }

            SelectRun(item);
        }, errorTitle: "Could not show the run", errorMode: ErrorMode.Toast, notifications: Services.Notifications).ConfigureAwait(true);
    }

    // ----- Commands list ----------------------------------------------------------------

    private void RebuildCommands()
    {
        var detected = Context.Profile?.Commands ?? [];
        var all = detected.Concat(_custom)
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in all)
        {
            seen.Add(command.Id);
            if (_rows.TryGetValue(command.Id, out var row))
            {
                if (row.Command != command)
                {
                    row.Command = command;
                }
            }
            else
            {
                _rows[command.Id] = new CommandRowViewModel(command);
            }
        }

        foreach (var removed in _rows.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _rows.Remove(removed);
        }

        CommandCount = _rows.Count;
        UpdateCommandStates();
        ApplyCommandFilter();
        OnPropertyChanged(nameof(ShowNoCommands));
        OnPropertyChanged(nameof(NoCommandsDescription));
    }

    private void ApplyCommandFilter()
    {
        var desired = new List<CommandListEntry>();
        var visible = 0;
        var groups = _rows.Values
            .Where(r => CategoryFilter is null || r.Category == CategoryFilter)
            .Where(r => r.Matches(SearchText))
            .GroupBy(r => r.Category)
            .OrderBy(g => CommandCategories.Rank(g.Key));
        foreach (var group in groups)
        {
            if (!_headers.TryGetValue(group.Key, out var header))
            {
                header = new CommandGroupHeader(group.Key);
                _headers[group.Key] = header;
            }

            var rows = group
                .OrderBy(r => r.IsCustom ? 1 : 0)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            header.Count = rows.Count;
            desired.Add(header);
            desired.AddRange(rows);
            visible += rows.Count;
        }

        Entries.SyncWith(desired);
        VisibleCommandCount = visible;
        if (SelectedEntry is not null && !Entries.Contains(SelectedEntry))
        {
            SelectedEntry = null;
        }
    }

    private void ShowCategory(CommandCategory category)
    {
        SearchText = string.Empty;
        CategoryFilter = _rows.Values.Any(r => r.Category == category) ? category : null;
        SelectedEntry = Entries.OfType<CommandRowViewModel>().FirstOrDefault(r => r.Category == category);
    }

    /// <summary>Running state and last result of every command, from the runs known to the tab.</summary>
    private void UpdateCommandStates()
    {
        var byCommand = _runs.Values
            .Where(r => r.CommandId is not null)
            .GroupBy(r => r.CommandId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var row in _rows.Values)
        {
            if (!byCommand.TryGetValue(row.Id, out var runs))
            {
                row.RunningRunId = null;
                row.LastStatus = null;
                row.LastRunAt = null;
                continue;
            }

            row.RunningRunId = runs.Where(r => r.IsRunning).MaxBy(r => r.StartedAt)?.Id;
            var last = runs.Where(r => r.IsFinished).MaxBy(r => r.StartedAt);
            row.LastStatus = last?.Status;
            row.LastRunAt = last?.EndedAt ?? last?.StartedAt;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyCommandFilter();

    partial void OnCategoryFilterChanged(CommandCategory? value) => ApplyCommandFilter();

    partial void OnSelectedEntryChanged(CommandListEntry? value)
    {
        if (value is { IsHeader: true })
        {
            SelectedEntry = null;
            return;
        }

        OnPropertyChanged(nameof(SelectedCommand));
    }

    // ----- Runs list --------------------------------------------------------------------

    private bool IsMine(IRunSession session) => string.Equals(session.Request.ProjectId, Context.ProjectId, StringComparison.Ordinal);

    private RunItemViewModel EnsureRun(IRunSession session, bool rebuild = true)
    {
        if (_runs.TryGetValue(session.Id, out var existing))
        {
            existing.Refresh(Now);
            return existing;
        }

        var item = new RunItemViewModel(session, Now);
        _runs[session.Id] = item;
        if (item.IsRunning && _subscribed.Add(session))
        {
            session.StatusChanged += OnSessionStatusChanged;
            session.ProgressChanged += OnSessionProgressChanged;
        }

        if (rebuild)
        {
            RebuildRuns();
            UpdateCommandStates();
            UpdateTicker();
        }

        return item;
    }

    private void RebuildRuns()
    {
        var running = _runs.Values.Where(r => r.IsRunning).OrderByDescending(r => r.StartedAt).ToList();
        var finished = _runs.Values.Where(r => !r.IsRunning).OrderByDescending(r => r.StartedAt).ToList();
        var desired = new List<RunListEntry>(running.Count + finished.Count + 2);
        if (running.Count > 0)
        {
            _runningHeader.CountText = running.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            desired.Add(_runningHeader);
            desired.AddRange(running);
        }

        if (finished.Count > 0)
        {
            desired.Add(_historyHeader);
            desired.AddRange(finished);
        }

        Runs.SyncWith(desired);
        RunCount = running.Count + finished.Count;
        RunningCount = running.Count;
        OnPropertyChanged(nameof(ShowNoRuns));
    }

    private void SelectNewestLiveRunIfIdle()
    {
        if (SelectedRun is { IsRunning: true })
        {
            return;
        }

        var newest = _runs.Values.Where(r => r.IsRunning).MaxBy(r => r.StartedAt);
        if (newest is not null && (SelectedRun is null || newest.StartedAt > SelectedRun.StartedAt))
        {
            SelectRun(newest);
        }
    }

    private void SelectRun(RunItemViewModel run)
    {
        if (!Runs.Contains(run))
        {
            RebuildRuns();
        }

        SelectedRunEntry = run;
    }

    partial void OnSelectedRunEntryChanged(RunListEntry? value)
    {
        if (value is RunGroupHeader)
        {
            SelectedRunEntry = SelectedRun;
            return;
        }

        var run = value as RunItemViewModel;
        if (ReferenceEquals(run, SelectedRun))
        {
            return;
        }

        Detail?.Dispose();
        Detail = null;
        SelectedRun = run;
        if (run is not null)
        {
            var detail = new RunDetailViewModel(this, run, LogBatchInterval);
            Detail = detail;
            _ = LoadDetailAsync(detail);
        }
    }

    private async Task LoadDetailAsync(RunDetailViewModel detail)
    {
        try
        {
            await detail.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the output of run {RunId}", detail.Run.Id);
        }
    }

    // ----- Live updates -----------------------------------------------------------------

    private void OnRunStarted(object? sender, IRunSession session)
    {
        if (!IsMine(session))
        {
            return;
        }

        Services.Dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var isNew = !_runs.ContainsKey(session.Id);
            var item = EnsureRun(session);

            // A run started from the header or the palette: show it unless the user is watching another live run.
            if (isNew && IsActive && SelectedRun is not { IsRunning: true })
            {
                SelectRun(item);
            }
        });
    }

    private void OnRunCompleted(object? sender, RunCompletedEventArgs e)
    {
        if (!string.Equals(e.Record.ProjectId, Context.ProjectId, StringComparison.Ordinal))
        {
            return;
        }

        Services.Dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (_runs.TryGetValue(e.Record.Id, out var item))
            {
                if (item.Session is { } session)
                {
                    Unsubscribe(session);
                }

                item.Apply(e.Record, Now);
            }
            else
            {
                item = new RunItemViewModel(e.Record, Now);
                _runs[e.Record.Id] = item;
            }

            if (ReferenceEquals(Detail?.Run, item))
            {
                Detail!.OnCompleted();
            }

            RebuildRuns();
            UpdateCommandStates();
            UpdateTicker();
        });
    }

    private void OnSessionStatusChanged(object? sender, EventArgs e)
    {
        if (sender is IRunSession session)
        {
            Services.Dispatcher.Post(() =>
            {
                if (!_disposed && _runs.TryGetValue(session.Id, out var item))
                {
                    var wasRunning = item.IsRunning;
                    item.Refresh(Now);
                    if (wasRunning != item.IsRunning)
                    {
                        RebuildRuns();
                        UpdateCommandStates();
                    }
                }
            });
        }
    }

    private void OnSessionProgressChanged(object? sender, EventArgs e)
    {
        if (sender is IRunSession session && IsActive)
        {
            Services.Dispatcher.Post(() =>
            {
                if (!_disposed && _runs.TryGetValue(session.Id, out var item))
                {
                    item.Refresh(Now);
                }
            });
        }
    }

    private void OnContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectContext.Profile):
                RebuildCommands();
                break;
            case nameof(ProjectContext.Project):
                OnPropertyChanged(nameof(NoCommandsDescription));
                break;
        }
    }

    // ----- Ticking ----------------------------------------------------------------------

    /// <summary>Every second while the tab is visible and a run is live: durations and the stalled check.</summary>
    internal void TickAll()
    {
        if (_disposed)
        {
            return;
        }

        var now = Now;
        foreach (var run in _runs.Values.Where(r => r.IsRunning))
        {
            run.Tick(now);
        }

        Detail?.Tick(now);
    }

    private void UpdateTicker()
    {
        var needed = IsActive && !_disposed && RunningCount > 0 && TickInterval != Timeout.InfiniteTimeSpan;
        if (needed && _ticker is null)
        {
            _ticker = Services.Time.CreateTimer(_ => Services.Dispatcher.Post(TickAll), null, TickInterval, TickInterval);
        }
        else if (!needed && _ticker is not null)
        {
            _ticker.Dispose();
            _ticker = null;
        }
    }

    private async Task<bool?> ShowEditorAsync(CommandEditorDialogViewModel dialog)
    {
        try
        {
            return await Services.Dialogs.ShowDialogAsync(dialog).ConfigureAwait(true);
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            Services.Notifications.ShowError(ErrorInfo.From(ex, "Could not open the command editor"));
            return false;
        }
    }
}
