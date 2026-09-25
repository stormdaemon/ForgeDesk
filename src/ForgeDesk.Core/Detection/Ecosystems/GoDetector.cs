using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Go: go.mod modules, go.work workspaces, main packages, well-known web frameworks.</summary>
internal sealed partial class GoDetector : IEcosystemDetector
{
    public const int MaxCommandEntryPoints = 4;
    private const int MaxSourceBytes = 256 * 1024;

    private static readonly (string ModulePrefix, string Name, TechnologyKind Kind)[] KnownModules =
    [
        ("github.com/gin-gonic/gin", "Gin", TechnologyKind.Framework),
        ("github.com/labstack/echo", "Echo", TechnologyKind.Framework),
        ("github.com/gofiber/fiber", "Fiber", TechnologyKind.Framework),
        ("github.com/go-chi/chi", "chi", TechnologyKind.Framework),
        ("github.com/spf13/cobra", "Cobra", TechnologyKind.Library),
    ];

    public string Name => "Go";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        if (!context.Exists("go.mod") && !context.Exists("go.work"))
        {
            return;
        }

        var evidence = context.Exists("go.mod") ? "go.mod" : "go.work";
        context.AddTechnology("Go", TechnologyKind.Language, evidence);
        context.AddBuildSystem("Go");
        if (context.Exists("go.work"))
        {
            context.IsMonorepo = true;
        }

        var goMod = context.Exists("go.mod") ? await context.ReadTextAsync("go.mod", cancellationToken).ConfigureAwait(false) : null;
        if (goMod is not null)
        {
            foreach (var module in RequiredModules(goMod))
            {
                foreach (var known in KnownModules.Where(k => module.StartsWith(k.ModulePrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    context.AddTechnology(known.Name, known.Kind, $"{module} in go.mod");
                }
            }
        }

        Add(context, "build", "Build", "go build ./...", CommandCategory.Build);
        Add(context, "test", "Test", "go test ./...", CommandCategory.Test);
        Add(context, "vet", "Vet", "go vet ./...", CommandCategory.Lint);
        Add(context, "fmt", "List unformatted files", "gofmt -l .", CommandCategory.Format);
        Add(context, "mod-download", "Download modules", "go mod download", CommandCategory.Install);

        if (await HasMainPackageAsync(context, string.Empty, cancellationToken).ConfigureAwait(false))
        {
            Add(context, "run", "Run", "go run .", CommandCategory.Run);
        }

        var entryPoints = context.Files
            .Where(f => f.StartsWith("cmd/", StringComparison.Ordinal) && RelativePaths.Depth(f) == 2 && f.EndsWith("/main.go", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(MaxCommandEntryPoints)
            .Select(RelativePaths.DirectoryOf);
        foreach (var entryPoint in entryPoints)
        {
            var name = RelativePaths.FileName(entryPoint);
            Add(context, $"run:{name}", $"Run {name}", $"go run ./{entryPoint}", CommandCategory.Run);
        }

        if (context.Files.Any(f => f.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)))
        {
            context.AddTestFramework("go test");
        }
    }

    /// <summary>Module paths from "require x v1" lines and "require ( … )" blocks.</summary>
    internal static IReadOnlyList<string> RequiredModules(string goMod)
    {
        var modules = new List<string>();
        var inBlock = false;
        foreach (var rawLine in goMod.Split('\n'))
        {
            var line = rawLine.Split("//", 2)[0].Trim();
            if (inBlock)
            {
                if (line.StartsWith(')'))
                {
                    inBlock = false;
                }
                else if (line.Length > 0)
                {
                    modules.Add(line.Split(' ', 2)[0]);
                }
            }
            else if (line.StartsWith("require", StringComparison.Ordinal))
            {
                var rest = line["require".Length..].Trim();
                if (rest.StartsWith('('))
                {
                    inBlock = true;
                }
                else if (rest.Length > 0)
                {
                    modules.Add(rest.Split(' ', 2)[0]);
                }
            }
        }

        return modules;
    }

    private static async Task<bool> HasMainPackageAsync(DetectionContext context, string directory, CancellationToken cancellationToken)
    {
        foreach (var file in context.FilesIn(directory).Where(f => f.EndsWith(".go", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)).Take(20))
        {
            var text = await context.ReadTextAsync(file, cancellationToken, MaxSourceBytes).ConfigureAwait(false);
            if (text is not null && PackageMain().IsMatch(text))
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category) =>
        context.AddCommand(new DetectedCommand { Id = $"go:{id}", Name = name, CommandLine = commandLine, Category = category, Source = "go.mod" });

    [GeneratedRegex(@"^\s*package\s+main\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PackageMain();
}
