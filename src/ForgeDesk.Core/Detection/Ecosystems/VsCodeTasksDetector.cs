using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>
/// VS Code tasks (.vscode/tasks.json, JSON with comments) of type "shell" or "process", with the
/// platform-specific overrides applied. Tasks that need editor context (${file}, ${input:…}) are skipped.
/// </summary>
internal sealed partial class VsCodeTasksDetector : IEcosystemDetector
{
    public const string TasksFile = ".vscode/tasks.json";
    public const int MaxTasks = 30;

    public string Name => "VS Code tasks";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        if (!context.Exists(TasksFile))
        {
            return;
        }

        var text = await context.ReadTextAsync(TasksFile, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return;
        }

        using var document = JsonLite.TryParse(text, out var error);
        if (document is null)
        {
            context.Notes.Add($"{TasksFile} could not be read ({error}); its tasks are not listed.");
            return;
        }

        if (document.RootElement.GetArray("tasks") is not { } tasks)
        {
            return;
        }

        var projectFolderName = Path.GetFileName(context.Root);
        var added = 0;
        foreach (var task in tasks.EnumerateArray())
        {
            if (added == MaxTasks)
            {
                break;
            }

            if (ToCommand(task, projectFolderName) is { } command)
            {
                context.AddCommand(command);
                added++;
            }
        }
    }

    internal static DetectedCommand? ToCommand(JsonElement task, string projectFolderName)
    {
        if (task.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var label = task.GetString("label") ?? task.GetString("taskName");
        var type = task.GetString("type") ?? "process";
        if (string.IsNullOrWhiteSpace(label) || (type != "shell" && type != "process"))
        {
            return null;
        }

        // Platform sections replace command/args/options for that OS.
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var overrides = task.GetObject(platform);
        var command = overrides?.GetString("command") ?? task.GetString("command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var args = overrides?.GetArray("args") ?? task.GetArray("args");
        var options = overrides?.GetObject("options") ?? task.GetObject("options");

        var executable = WithPlatformSeparators(Expand(command, projectFolderName));
        var parts = new List<string> { type == "process" ? CommandLines.Quote(executable) : executable };
        if (args is { } argArray)
        {
            foreach (var arg in argArray.EnumerateArray())
            {
                var value = arg.ValueKind == JsonValueKind.String ? arg.GetString() : arg.GetString("value");
                if (value is not null)
                {
                    parts.Add(CommandLines.Quote(Expand(value, projectFolderName)));
                }
            }
        }

        var commandLine = string.Join(' ', parts);
        var workingDirectory = options?.GetString("cwd") is { } cwd ? RelativeWorkingDirectory(Expand(cwd, projectFolderName)) : string.Empty;
        if (UnresolvedVariable().IsMatch(commandLine) || workingDirectory is null)
        {
            return null;
        }

        return new DetectedCommand
        {
            Id = $"vscode:{label}",
            Name = label,
            CommandLine = commandLine,
            Category = GroupCategory(task) ?? CommandCategorizer.FromName(label),
            Source = TasksFile,
            WorkingDirectory = workingDirectory,
            Description = task.GetString("detail"),
        };
    }

    private static CommandCategory? GroupCategory(JsonElement task)
    {
        if (!task.TryGetProperty("group", out var group))
        {
            return null;
        }

        var kind = group.ValueKind == JsonValueKind.String ? group.GetString() : group.GetString("kind");
        return kind switch
        {
            "build" => CommandCategory.Build,
            "test" => CommandCategory.Test,
            _ => null,
        };
    }

    /// <summary>ForgeDesk runs commands from the project root, which is what ${workspaceFolder} means here.</summary>
    private static string Expand(string value, string projectFolderName) =>
        value
            .Replace("${workspaceFolder}/", string.Empty, StringComparison.Ordinal)
            .Replace("${workspaceFolder}\\", string.Empty, StringComparison.Ordinal)
            .Replace("${workspaceFolder}", ".", StringComparison.Ordinal)
            .Replace("${workspaceRoot}", ".", StringComparison.Ordinal)
            .Replace("${workspaceFolderBasename}", projectFolderName, StringComparison.Ordinal)
            .Replace("${pathSeparator}", Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal);

    /// <summary>
    /// cmd.exe reads "tools/gen.exe" as "tools" followed by a "/gen.exe" switch: on Windows the
    /// program path (the first word) needs backslashes. Arguments are left alone.
    /// </summary>
    private static string WithPlatformSeparators(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return command;
        }

        var end = command.IndexOfAny([' ', '\t']);
        var program = end < 0 ? command : command[..end];
        if (!program.Contains('/', StringComparison.Ordinal) || program.Contains("://", StringComparison.Ordinal))
        {
            return command;
        }

        return program.Replace('/', '\\') + (end < 0 ? string.Empty : command[end..]);
    }

    /// <summary>Relative, forward-slash working directory; null when it points outside the project.</summary>
    private static string? RelativeWorkingDirectory(string cwd)
    {
        var normalized = cwd.Replace('\\', '/').Trim().TrimEnd('/');
        if (normalized is "" or ".")
        {
            return string.Empty;
        }

        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return Path.IsPathRooted(normalized) || normalized.Split('/').Contains("..") ? null : normalized;
    }

    [GeneratedRegex(@"\$\{[^}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex UnresolvedVariable();
}
