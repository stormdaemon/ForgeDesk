using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Projects;

/// <summary>The registered projects, persisted in the projects table, with JSON caches of derived data.</summary>
internal sealed partial class ProjectRegistry : IProjectRegistry
{
    public const int MaxNameLength = 100;
    public const int MaxGroupLength = 100;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Columns = "id, name, path, added_at, last_opened_at, is_pinned, group_name, color, sort_order, github_owner, github_repo";

    private readonly Database _database;
    private readonly IGitService _git;
    private readonly IActivityLog _activity;
    private readonly IServiceProvider _services;
    private readonly IClock _clock;
    private readonly ILogger<ProjectRegistry> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ProjectRegistry(Database database, IGitService git, IActivityLog activity, IServiceProvider services, IClock clock, ILogger<ProjectRegistry>? logger = null)
    {
        _database = database;
        _git = git;
        _activity = activity;
        _services = services;
        _clock = clock;
        _logger = logger ?? NullLogger<ProjectRegistry>.Instance;
    }

    public event EventHandler<ProjectsChangedEventArgs>? Changed;

    /// <summary>Source of randomness for avatar colors (replaceable for deterministic tests).</summary>
    internal Random Random { get; init; } = Random.Shared;

    public async Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _database.UseAsync(c => c.QueryAsync<ProjectRow>(new CommandDefinition(
            $"SELECT {Columns} FROM projects ORDER BY sort_order, name COLLATE NOCASE", cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        return rows.Select(ToProject).ToList();
    }

    public async Task<Project?> GetAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var row = await _database.UseAsync(c => c.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            $"SELECT {Columns} FROM projects WHERE id = @Id", new { Id = projectId }, cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToProject(row);
    }

    public async Task<Project?> FindByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = ProjectFolder.TryNormalize(path);
        if (normalized is null)
        {
            return null;
        }

        var rows = await _database.UseAsync(c => QueryByPathAsync(c, normalized, cancellationToken), cancellationToken).ConfigureAwait(false);
        var match = rows.FirstOrDefault(r => string.Equals(r.Path, normalized, PathUtil.Comparison));
        return match is null ? null : ToProject(match);
    }

    public async Task<Project> AddAsync(string path, string? displayName = null, CancellationToken cancellationToken = default)
    {
        var normalized = ProjectFolder.Validate(path);
        var name = string.IsNullOrWhiteSpace(displayName) ? DefaultName(normalized) : ValidateName(displayName);

        // Fail fast before asking git anything; the check is repeated under the write lock below.
        var registered = await _database.UseAsync(c => QueryByPathAsync(c, normalized, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (registered.FirstOrDefault() is { } alreadyRegistered)
        {
            throw AlreadyRegistered(alreadyRegistered, normalized);
        }

        var gitHub = await GitHubLinkResolver.ResolveAsync(_git, normalized, _logger, cancellationToken).ConfigureAwait(false);

        Project project;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            project = await _database.UseAsync(async c =>
            {
                var clash = (await QueryByPathAsync(c, normalized, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
                if (clash is not null)
                {
                    throw AlreadyRegistered(clash, normalized);
                }

                var usedColors = await c.QueryAsync<string?>(new CommandDefinition("SELECT color FROM projects", cancellationToken: cancellationToken)).ConfigureAwait(false);
                var nextSortOrder = await c.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM projects", cancellationToken: cancellationToken)).ConfigureAwait(false);
                var row = new ProjectRow
                {
                    Id = Ids.New(),
                    Name = name,
                    Path = normalized,
                    AddedAt = _clock.Now,
                    Color = AvatarPalette.Pick(usedColors, Random),
                    SortOrder = nextSortOrder,
                    GithubOwner = gitHub?.Owner,
                    GithubRepo = gitHub?.Name,
                };

                await c.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO projects (id, name, path, added_at, last_opened_at, is_pinned, group_name, color, sort_order, github_owner, github_repo)
                    VALUES (@Id, @Name, @Path, @AddedAt, NULL, 0, NULL, @Color, @SortOrder, @GithubOwner, @GithubRepo)
                    """,
                    row, cancellationToken: cancellationToken)).ConfigureAwait(false);
                return ToProject(row);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex) when (IsConstraintViolation(ex))
        {
            throw new ForgeException(ErrorKind.AlreadyExists, $"The folder '{normalized}' is already in ForgeDesk.",
                "Open it from the dashboard or the sidebar.", ex.Detail, ex);
        }
        finally
        {
            _writeGate.Release();
        }

        await ActivityRecorder.TryRecordAsync(_activity, new ActivityEntry
        {
            ProjectId = project.Id,
            At = _clock.Now,
            Kind = ActivityKind.ProjectAdded,
            Outcome = ActivityOutcome.Success,
            Title = $"Added {project.Name}",
            Detail = project.Path,
        }, _logger).ConfigureAwait(false);

        RaiseChanged(project.Id);
        return project;
    }

    public async Task RemoveAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return;
        }

        await DeleteRunHistoryAsync(projectId, cancellationToken).ConfigureAwait(false);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Tasks, runs, custom commands and activity rows go with the project (ON DELETE CASCADE).
            await _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM projects WHERE id = @Id", new { Id = projectId }, cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        // Not attached to the project, so the entry survives the cascade and stays in the global journal.
        await ActivityRecorder.TryRecordAsync(_activity, new ActivityEntry
        {
            ProjectId = null,
            At = _clock.Now,
            Kind = ActivityKind.ProjectRemoved,
            Outcome = ActivityOutcome.Info,
            Title = $"Removed {project.Name}",
            Detail = project.Path,
        }, _logger).ConfigureAwait(false);

        RaiseChanged(projectId);
    }

    public async Task<Project> UpdateAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var name = ValidateName(project.Name);
        var group = ValidateGroup(project.Group);
        var color = ValidateColor(project.Color);

        var affected = await _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE projects
               SET name = @Name, group_name = @Group, color = @Color, is_pinned = @IsPinned, sort_order = @SortOrder
             WHERE id = @Id
            """,
            new { project.Id, Name = name, Group = group, Color = color, IsPinned = project.IsPinned ? 1 : 0, project.SortOrder },
            cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw ProjectNotFound();
        }

        var updated = await GetAsync(project.Id, cancellationToken).ConfigureAwait(false) ?? throw ProjectNotFound();
        RaiseChanged(project.Id);
        return updated;
    }

    public async Task<Project> RelocateAsync(string projectId, string newPath, CancellationToken cancellationToken = default)
    {
        var project = await GetAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw ProjectNotFound();
        var normalized = ProjectFolder.Validate(newPath);
        var gitHub = await GitHubLinkResolver.ResolveAsync(_git, normalized, _logger, cancellationToken).ConfigureAwait(false);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.UseAsync(async c =>
            {
                var clash = (await QueryByPathAsync(c, normalized, cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Id != projectId);
                if (clash is not null)
                {
                    throw AlreadyRegistered(clash, normalized);
                }

                // The cached snapshot describes the old location (typically "folder missing"): drop it.
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE projects
                       SET path = @Path, github_owner = @Owner, github_repo = @Repo, snapshot_json = NULL, snapshot_at = NULL
                     WHERE id = @Id
                    """,
                    new { Id = projectId, Path = normalized, Owner = gitHub?.Owner, Repo = gitHub?.Name },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex) when (IsConstraintViolation(ex))
        {
            throw new ForgeException(ErrorKind.AlreadyExists, $"The folder '{normalized}' is already used by another project.",
                "Remove the other project first, or choose a different folder.", ex.Detail, ex);
        }
        finally
        {
            _writeGate.Release();
        }

        var relocated = project with { Path = normalized, GitHub = gitHub };
        await ActivityRecorder.TryRecordAsync(_activity, new ActivityEntry
        {
            ProjectId = projectId,
            At = _clock.Now,
            Kind = ActivityKind.ProjectRelocated,
            Outcome = ActivityOutcome.Success,
            Title = $"Relocated {project.Name}",
            Detail = $"{project.Path} → {normalized}",
        }, _logger).ConfigureAwait(false);

        RaiseChanged(projectId);
        return relocated;
    }

    public async Task MarkOpenedAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var affected = await _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
            "UPDATE projects SET last_opened_at = @Now WHERE id = @Id",
            new { Id = projectId, Now = _clock.Now }, cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        if (affected > 0)
        {
            RaiseChanged(projectId);
        }
    }

    public Task<ProjectSnapshot?> GetCachedSnapshotAsync(string projectId, CancellationToken cancellationToken = default) =>
        ReadCacheAsync<ProjectSnapshot>(CacheColumn.Snapshot, projectId, cancellationToken);

    public Task SaveSnapshotAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return WriteCacheAsync(CacheColumn.Snapshot, snapshot.ProjectId, snapshot, snapshot.CapturedAt, cancellationToken);
    }

    public Task<ProjectProfile?> GetCachedProfileAsync(string projectId, CancellationToken cancellationToken = default) =>
        ReadCacheAsync<ProjectProfile>(CacheColumn.Profile, projectId, cancellationToken);

    public Task SaveProfileAsync(string projectId, ProjectProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return WriteCacheAsync(CacheColumn.Profile, projectId, profile, profile.DetectedAt, cancellationToken);
    }

    public Task<ProjectHealthReport?> GetCachedHealthAsync(string projectId, CancellationToken cancellationToken = default) =>
        ReadCacheAsync<ProjectHealthReport>(CacheColumn.Health, projectId, cancellationToken);

    public Task SaveHealthAsync(string projectId, ProjectHealthReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return WriteCacheAsync(CacheColumn.Health, projectId, report, report.GeneratedAt, cancellationToken);
    }

    internal static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "The project name cannot be empty.", "Type a name, for example the folder name.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"The project name is too long (maximum {MaxNameLength} characters).");
        }

        return trimmed;
    }

    private static string? ValidateGroup(string? group)
    {
        var trimmed = group?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxGroupLength)
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"The group name is too long (maximum {MaxGroupLength} characters).");
        }

        return trimmed;
    }

    private static string? ValidateColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var trimmed = color.Trim();
        if (!HexColor().IsMatch(trimmed))
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"'{trimmed}' is not a valid color.", "Use the #RRGGBB format, for example #2F80ED.");
        }

        return trimmed.ToUpperInvariant();
    }

    private static string DefaultName(string normalizedPath)
    {
        var name = Path.GetFileName(normalizedPath);
        name = string.IsNullOrWhiteSpace(name) ? normalizedPath : name;
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    /// <summary>
    /// Registered projects whose path equals <paramref name="normalizedPath"/> ignoring case. SQLite's
    /// NOCASE only folds A-Z while NTFS also folds "É"/"é", so the comparison is done here with
    /// OrdinalIgnoreCase (the registry holds a handful of rows; the unique index still guards A-Z).
    /// </summary>
    private static async Task<IEnumerable<ProjectRow>> QueryByPathAsync(SqliteConnection connection, string normalizedPath, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            $"SELECT {Columns} FROM projects", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Where(r => string.Equals(r.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// The path index ignores case (NTFS does). On case-sensitive file systems two folders that only
    /// differ by case therefore cannot both be registered; say so instead of calling them identical.
    /// </summary>
    private static ForgeException AlreadyRegistered(ProjectRow existing, string requestedPath) =>
        string.Equals(existing.Path, requestedPath, PathUtil.Comparison)
            ? new ForgeException(ErrorKind.AlreadyExists, $"'{existing.Name}' is already in ForgeDesk.", "Open it from the dashboard or the sidebar.")
            : new ForgeException(ErrorKind.AlreadyExists,
                $"'{existing.Name}' ({existing.Path}) is already in ForgeDesk, and its path only differs from '{requestedPath}' by letter case.",
                "ForgeDesk treats such paths as the same folder. Rename one of the folders to add both.");

    private static bool IsConstraintViolation(ForgeException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    private static ForgeException ProjectNotFound() =>
        new(ErrorKind.NotFound, "This project is no longer in ForgeDesk.", "It may have been removed from another window. Refresh the dashboard.");

    private async Task DeleteRunHistoryAsync(string projectId, CancellationToken cancellationToken)
    {
        // Resolved lazily: the run service may itself depend on the registry.
        if (_services.GetService(typeof(IRunService)) is not IRunService runs)
        {
            return;
        }

        try
        {
            await runs.DeleteHistoryAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Leftover log files are harmless; removing the project matters more.
            _logger.LogWarning(ex, "Could not delete the run history of project {ProjectId}", projectId);
        }
    }

    private async Task<T?> ReadCacheAsync<T>(CacheColumn column, string projectId, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var sql = column switch
        {
            CacheColumn.Snapshot => "SELECT snapshot_json FROM projects WHERE id = @Id",
            CacheColumn.Profile => "SELECT profile_json FROM projects WHERE id = @Id",
            _ => "SELECT health_json FROM projects WHERE id = @Id",
        };
        var json = await _database.UseAsync(c => c.ExecuteScalarAsync<string?>(new CommandDefinition(
            sql, new { Id = projectId }, cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
        {
            // A cache is disposable: a damaged or outdated entry is simply recomputed.
            _logger.LogWarning(ex, "Ignoring unreadable {Column} cache of project {ProjectId}", column, projectId);
            return null;
        }
    }

    private async Task WriteCacheAsync<T>(CacheColumn column, string projectId, T value, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var sql = column switch
        {
            CacheColumn.Snapshot => "UPDATE projects SET snapshot_json = @Json, snapshot_at = @At WHERE id = @Id",
            CacheColumn.Profile => "UPDATE projects SET profile_json = @Json, profile_at = @At WHERE id = @Id",
            _ => "UPDATE projects SET health_json = @Json, health_at = @At WHERE id = @Id",
        };
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var timestamp = at == default ? _clock.Now : at;

        // A project removed meanwhile simply updates no row: nothing to cache any more.
        await _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
            sql, new { Id = projectId, Json = json, At = timestamp }, cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
    }

    private void RaiseChanged(string projectId) => SafeEvent.Raise(Changed, this, new ProjectsChangedEventArgs(projectId));

    private static Project ToProject(ProjectRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Path = row.Path,
        AddedAt = row.AddedAt,
        LastOpenedAt = row.LastOpenedAt,
        IsPinned = row.IsPinned != 0,
        Group = row.GroupName,
        Color = row.Color,
        SortOrder = (int)Math.Clamp(row.SortOrder, int.MinValue, int.MaxValue),
        GitHub = !string.IsNullOrEmpty(row.GithubOwner) && !string.IsNullOrEmpty(row.GithubRepo) ? new GitHubRepoRef(row.GithubOwner, row.GithubRepo) : null,
    };

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    private enum CacheColumn
    {
        Snapshot,
        Profile,
        Health,
    }

    private sealed class ProjectRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public DateTimeOffset AddedAt { get; set; }
        public DateTimeOffset? LastOpenedAt { get; set; }
        public long IsPinned { get; set; }
        public string? GroupName { get; set; }
        public string? Color { get; set; }
        public long SortOrder { get; set; }
        public string? GithubOwner { get; set; }
        public string? GithubRepo { get; set; }
    }
}
