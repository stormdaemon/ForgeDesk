using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Task (taskfile.dev): public tasks of the root Taskfile.</summary>
internal sealed class TaskfileDetector : IEcosystemDetector
{
    public const int MaxTasks = 30;

    public string Name => "Task";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var taskfile = context.FirstExisting("Taskfile.yml", "Taskfile.yaml", "taskfile.yml", "taskfile.yaml", "Taskfile.dist.yml", "Taskfile.dist.yaml");
        if (taskfile is null)
        {
            return;
        }

        context.AddBuildSystem("Task");
        context.AddTechnology("Task", TechnologyKind.BuildTool, taskfile);
        var text = await context.ReadTextAsync(taskfile, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return;
        }

        foreach (var (name, description) in Tasks(text).Take(MaxTasks))
        {
            context.AddCommand(new DetectedCommand
            {
                Id = $"task:{name}",
                Name = name,
                CommandLine = $"task {CommandLines.Quote(name)}",
                Category = CommandCategorizer.FromName(name),
                Source = taskfile,
                Description = description,
            });
        }
    }

    internal static IReadOnlyList<(string Name, string? Description)> Tasks(string taskfile)
    {
        var lines = YamlOutline.Read(taskfile);
        var tasksPosition = YamlOutline.FindTopLevel(lines, "tasks");
        if (tasksPosition < 0)
        {
            return [];
        }

        var result = new List<(string, string?)>();
        foreach (var task in YamlOutline.Children(lines, tasksPosition).Where(l => !l.IsListItem && l.Key is not null))
        {
            var position = IndexOf(lines, task);
            var body = YamlOutline.Descendants(lines, position);
            var properties = body.Where(l => l.Indent == (body.Count > 0 ? body[0].Indent : 0) && !l.IsListItem).ToList();
            var isInternal = properties.Any(p => p.Key == "internal" && p.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (isInternal || task.Key!.StartsWith('_'))
            {
                continue;
            }

            var description = properties.FirstOrDefault(p => p.Key is "desc" or "summary")?.Value;
            result.Add((task.Key, description is null ? null : YamlOutline.Unquote(description)));
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<YamlLine> lines, YamlLine line)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (ReferenceEquals(lines[i], line))
            {
                return i;
            }
        }

        return -1;
    }
}
