using Dapper;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ForgeDesk.Core.Detection;

/// <summary>User-defined commands per project, stored in the custom_commands table.</summary>
internal sealed class CustomCommandStore : ICustomCommandStore
{
    public const string IdPrefix = "custom:";
    public const string SourceName = "custom";
    public const int MaxNameLength = 200;
    public const int MaxCommandLineLength = 8000;

    private readonly Database _database;
    private readonly IClock _clock;

    public CustomCommandStore(Database database, IClock clock)
    {
        _database = database;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DetectedCommand>> GetAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var rows = await _database.UseAsync(c => c.QueryAsync<CustomCommandRow>(new CommandDefinition(
            "SELECT id, name, command_line, working_dir, category FROM custom_commands WHERE project_id = @ProjectId ORDER BY created_at, name",
            new { ProjectId = projectId }, cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        return rows.Select(ToCommand).ToList();
    }

    public async Task<DetectedCommand> AddAsync(string projectId, string name, string commandLine, CommandCategory category, string? workingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var row = new CustomCommandRow
        {
            Id = Ids.New(),
            Name = ValidateName(name),
            CommandLine = ValidateCommandLine(commandLine),
            WorkingDir = NormalizeWorkingDirectory(workingDirectory),
            Category = (long)ValidateCategory(category),
        };

        try
        {
            await _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO custom_commands (id, project_id, name, command_line, working_dir, category, created_at)
                VALUES (@Id, @ProjectId, @Name, @CommandLine, @WorkingDir, @Category, @CreatedAt)
                """,
                new { row.Id, ProjectId = projectId, row.Name, row.CommandLine, row.WorkingDir, row.Category, CreatedAt = _clock.Now },
                cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
        }
        catch (ForgeException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Foreign key violation: the project was removed meanwhile.
            throw new ForgeException(ErrorKind.NotFound, "This project is no longer in ForgeDesk.", "Add the project again, then create the command.", ex.Detail, ex);
        }

        return ToCommand(row);
    }

    public async Task UpdateAsync(string projectId, DetectedCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(command);
        var id = StorageId(command.Id);
        var affected = await _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
            """
            UPDATE custom_commands
               SET name = @Name, command_line = @CommandLine, working_dir = @WorkingDir, category = @Category
             WHERE id = @Id AND project_id = @ProjectId
            """,
            new
            {
                Id = id,
                ProjectId = projectId,
                Name = ValidateName(command.Name),
                CommandLine = ValidateCommandLine(command.CommandLine),
                WorkingDir = NormalizeWorkingDirectory(command.WorkingDirectory),
                Category = (long)ValidateCategory(command.Category),
            },
            cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);

        if (affected == 0)
        {
            throw new ForgeException(ErrorKind.NotFound, $"The command '{command.Name}' no longer exists.", "It may have been deleted. Refresh the list of commands.");
        }
    }

    public async Task DeleteAsync(string projectId, string commandId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var id = StorageId(commandId);
        await _database.UseAsync(c => c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM custom_commands WHERE id = @Id AND project_id = @ProjectId",
            new { Id = id, ProjectId = projectId }, cancellationToken: cancellationToken)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a user-entered working directory: relative to the project, forward slashes,
    /// never escaping the project folder. Returns null for the project root.
    /// </summary>
    internal static string? NormalizeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var normalized = workingDirectory.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.Trim('/');
        if (normalized is "" or ".")
        {
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(workingDirectory.Trim()) || workingDirectory.Trim().StartsWith('/') || normalized.Contains(':', StringComparison.Ordinal)
            || segments.Any(s => s == ".."))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "The working directory must be a folder inside the project.",
                "Use a path relative to the project root, such as \"src/web\", or leave it empty to run from the root.");
        }

        return string.Join('/', segments.Where(s => s != "."));
    }

    private static string StorageId(string commandId)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        if (!commandId.StartsWith(IdPrefix, StringComparison.Ordinal) || commandId.Length == IdPrefix.Length)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Only commands you created can be edited or deleted.",
                "Detected commands come from the project's files; change them there instead.");
        }

        return commandId[IdPrefix.Length..];
    }

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Give the command a name.", "The name is what appears in the list of commands, e.g. \"Start API\".");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"The command name is too long (maximum {MaxNameLength} characters).");
        }

        return trimmed;
    }

    private static string ValidateCommandLine(string commandLine)
    {
        var trimmed = commandLine?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ForgeException(ErrorKind.InvalidInput, "Enter the command to run.", "For example \"npm run dev\" or \"dotnet watch run\".");
        }

        if (trimmed.Length > MaxCommandLineLength)
        {
            throw new ForgeException(ErrorKind.InvalidInput, $"The command is too long (maximum {MaxCommandLineLength} characters).",
                "Put long command sequences in a script file and run that script instead.");
        }

        if (trimmed.Contains('\n', StringComparison.Ordinal) || trimmed.Contains('\r', StringComparison.Ordinal))
        {
            throw new ForgeException(ErrorKind.InvalidInput, "The command must fit on a single line.",
                "Chain commands with && or put them in a script file.");
        }

        return trimmed;
    }

    private static CommandCategory ValidateCategory(CommandCategory category) =>
        Enum.IsDefined(category) ? category : CommandCategory.Other;

    private static DetectedCommand ToCommand(CustomCommandRow row) => new()
    {
        Id = IdPrefix + row.Id,
        Name = row.Name,
        CommandLine = row.CommandLine,
        Category = Enum.IsDefined((CommandCategory)row.Category) ? (CommandCategory)row.Category : CommandCategory.Other,
        Source = SourceName,
        WorkingDirectory = row.WorkingDir ?? string.Empty,
        IsCustom = true,
    };

    private sealed class CustomCommandRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string CommandLine { get; set; } = string.Empty;
        public string? WorkingDir { get; set; }
        public long Category { get; set; }
    }
}
