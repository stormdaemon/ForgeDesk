using System.Text.Json;
using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Deno: tasks of deno.json / deno.jsonc and the built-in test runner.</summary>
internal sealed class DenoDetector : IEcosystemDetector
{
    public string Name => "Deno";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var config = context.FirstExisting("deno.json", "deno.jsonc");
        if (config is null)
        {
            return;
        }

        context.AddBuildSystem("Deno");
        context.AddTechnology("Deno", TechnologyKind.Runtime, config);
        context.AddTechnology("TypeScript", TechnologyKind.Language, config);

        var taskNames = new List<string>();
        if (await context.ReadTextAsync(config, cancellationToken).ConfigureAwait(false) is { } text)
        {
            using var document = JsonLite.TryParse(text, out var error);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                context.Notes.Add($"{config} could not be read ({error ?? "not a JSON object"}); its tasks are not listed.");
            }
            else if (document.RootElement.GetObject("tasks") is { } tasks)
            {
                foreach (var task in tasks.EnumerateObject())
                {
                    // Deno 2 allows { "command": "...", "description": "..." } besides plain strings.
                    var description = task.Value.ValueKind == JsonValueKind.String ? task.Value.GetString() : task.Value.GetString("description") ?? task.Value.GetString("command");
                    taskNames.Add(task.Name);
                    context.AddCommand(new DetectedCommand
                    {
                        Id = $"deno:{task.Name}",
                        Name = task.Name,
                        CommandLine = $"deno task {CommandLines.Quote(task.Name)}",
                        Category = CommandCategorizer.FromName(task.Name),
                        Source = config,
                        Description = description,
                    });
                }
            }
        }

        var hasTestFiles = context.Files.Any(f => f.EndsWith("_test.ts", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith("_test.js", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase));
        if (hasTestFiles)
        {
            context.AddTestFramework("Deno test");
            if (!taskNames.Contains("test", StringComparer.Ordinal))
            {
                context.AddCommand(new DetectedCommand { Id = "deno:test", Name = "Test", CommandLine = "deno test", Category = CommandCategory.Test, Source = config });
            }
        }
    }
}
