using System.Text.Json;
using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>PHP: composer.json scripts and dependencies, Laravel (artisan), PHPUnit.</summary>
internal sealed class PhpDetector : IEcosystemDetector
{
    public string Name => "PHP";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var hasComposer = context.Exists("composer.json");
        var hasArtisan = context.Exists("artisan");
        if (!hasComposer && !hasArtisan)
        {
            return;
        }

        context.AddTechnology("PHP", TechnologyKind.Language, hasComposer ? "composer.json" : "artisan");

        var scripts = new List<KeyValuePair<string, string>>();
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasComposer && await context.ReadTextAsync("composer.json", cancellationToken).ConfigureAwait(false) is { } text)
        {
            using var document = JsonLite.TryParse(text, out var error);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
            {
                context.Notes.Add($"composer.json could not be read ({error ?? "not a JSON object"}); its scripts are not listed.");
            }
            else
            {
                var root = document.RootElement;
                packages.UnionWith(root.PropertyNames("require"));
                packages.UnionWith(root.PropertyNames("require-dev"));
                if (root.GetObject("scripts") is { } scriptObject)
                {
                    foreach (var script in scriptObject.EnumerateObject())
                    {
                        var body = script.Value.ValueKind switch
                        {
                            JsonValueKind.String => script.Value.GetString() ?? string.Empty,
                            JsonValueKind.Array => string.Join(" && ", script.Value.StringItems()),
                            _ => string.Empty,
                        };
                        scripts.Add(new KeyValuePair<string, string>(script.Name, body));
                    }
                }
            }
        }

        if (hasComposer)
        {
            context.AddBuildSystem("Composer");
            context.AddTechnology("Composer", TechnologyKind.PackageManager, "composer.json");
            Add(context, "composer:install", "Install dependencies", "composer install", CommandCategory.Install, "composer.json");
        }

        foreach (var (name, body) in scripts)
        {
            // "pre-install-cmd", "post-autoload-dump"…: Composer event hooks, not tasks.
            if (name.StartsWith("pre-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("post-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.AddCommand(new DetectedCommand
            {
                Id = $"composer:{name}",
                Name = name,
                CommandLine = $"composer run-script {CommandLines.Quote(name)}",
                Category = CommandCategorizer.FromName(name),
                Source = "composer.json",
                Description = body.Length > 200 ? body[..200] + "…" : body,
            });
        }

        var isLaravel = packages.Contains("laravel/framework") || hasArtisan;
        if (isLaravel)
        {
            context.AddTechnology("Laravel", TechnologyKind.Framework, hasArtisan ? "artisan" : "composer.json");
            if (hasArtisan)
            {
                Add(context, "artisan:serve", "Laravel server", "php artisan serve", CommandCategory.Dev, "artisan");
                Add(context, "artisan:test", "Laravel tests", "php artisan test", CommandCategory.Test, "artisan");
                Add(context, "artisan:migrate", "Apply migrations", "php artisan migrate", CommandCategory.Run, "artisan");
            }
        }

        var phpUnitConfig = context.FirstExisting("phpunit.xml", "phpunit.xml.dist", "phpunit.dist.xml");
        if (packages.Contains("phpunit/phpunit") || phpUnitConfig is not null)
        {
            context.AddTechnology("PHPUnit", TechnologyKind.TestFramework, phpUnitConfig ?? "composer.json");
            context.AddTestFramework("PHPUnit");
            if (!scripts.Any(s => s.Key == "test") && !(isLaravel && hasArtisan))
            {
                Add(context, "phpunit:test", "PHPUnit", CommandLines.ProjectScript("vendor/bin/phpunit"), CommandCategory.Test, phpUnitConfig ?? "composer.json");
            }
        }

        if (packages.Contains("pestphp/pest"))
        {
            context.AddTestFramework("Pest");
        }
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category, string source) =>
        context.AddCommand(new DetectedCommand { Id = id, Name = name, CommandLine = commandLine, Category = category, Source = source });
}
