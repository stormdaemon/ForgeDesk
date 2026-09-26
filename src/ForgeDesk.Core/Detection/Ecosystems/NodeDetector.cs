using System.Collections.Frozen;
using System.Text.Json;
using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Node.js / JavaScript / TypeScript: package.json scripts, package manager, frameworks, workspaces.</summary>
internal sealed class NodeDetector : IEcosystemDetector
{
    public const int MaxNestedDepth = 3;
    public const int MaxNestedPackages = 20;

    // npm runs these automatically around install/publish; they are not tasks a developer launches.
    private static readonly FrozenSet<string> LifecycleScripts = new[]
    {
        "preinstall", "install", "postinstall", "prepare", "prepublish", "prepublishOnly", "prepack", "postpack",
        "preversion", "version", "postversion", "dependencies",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly (string Package, bool IsPrefix, string Name, TechnologyKind Kind, bool IsTest)[] KnownPackages =
    [
        ("react", false, "React", TechnologyKind.Framework, false),
        ("next", false, "Next.js", TechnologyKind.Framework, false),
        ("vue", false, "Vue", TechnologyKind.Framework, false),
        ("nuxt", false, "Nuxt", TechnologyKind.Framework, false),
        ("svelte", false, "Svelte", TechnologyKind.Framework, false),
        ("@sveltejs/kit", false, "SvelteKit", TechnologyKind.Framework, false),
        ("@angular/core", false, "Angular", TechnologyKind.Framework, false),
        ("solid-js", false, "Solid", TechnologyKind.Framework, false),
        ("astro", false, "Astro", TechnologyKind.Framework, false),
        ("@remix-run/", true, "Remix", TechnologyKind.Framework, false),
        ("express", false, "Express", TechnologyKind.Framework, false),
        ("fastify", false, "Fastify", TechnologyKind.Framework, false),
        ("@nestjs/core", false, "NestJS", TechnologyKind.Framework, false),
        ("electron", false, "Electron", TechnologyKind.Framework, false),
        ("@tauri-apps/", true, "Tauri", TechnologyKind.Framework, false),
        ("vite", false, "Vite", TechnologyKind.BuildTool, false),
        ("webpack", false, "Webpack", TechnologyKind.BuildTool, false),
        ("esbuild", false, "esbuild", TechnologyKind.BuildTool, false),
        ("typescript", false, "TypeScript", TechnologyKind.Language, false),
        ("tailwindcss", false, "Tailwind CSS", TechnologyKind.Library, false),
        ("jest", false, "Jest", TechnologyKind.TestFramework, true),
        ("vitest", false, "Vitest", TechnologyKind.TestFramework, true),
        ("mocha", false, "Mocha", TechnologyKind.TestFramework, true),
        ("@playwright/test", false, "Playwright", TechnologyKind.TestFramework, true),
        ("playwright", false, "Playwright", TechnologyKind.TestFramework, true),
        ("cypress", false, "Cypress", TechnologyKind.TestFramework, true),
        ("eslint", false, "ESLint", TechnologyKind.Tooling, false),
        ("prettier", false, "Prettier", TechnologyKind.Tooling, false),
        ("storybook", false, "Storybook", TechnologyKind.Tooling, false),
        ("@storybook/", true, "Storybook", TechnologyKind.Tooling, false),
        ("turbo", false, "Turborepo", TechnologyKind.Tooling, false),
        ("nx", false, "Nx", TechnologyKind.Tooling, false),
    ];

    public string Name => "Node.js";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var manifests = context.FilesNamed("package.json")
            .OrderBy(RelativePaths.Depth)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (manifests.Count == 0 && !context.Exists("pnpm-workspace.yaml"))
        {
            return;
        }

        context.AddTechnology("Node.js", TechnologyKind.Runtime, manifests.FirstOrDefault() ?? "pnpm-workspace.yaml");
        if (context.Exists("tsconfig.json"))
        {
            context.AddTechnology("TypeScript", TechnologyKind.Language, "tsconfig.json");
        }

        var rootManifest = manifests.Contains("package.json") ? await ReadAsync(context, "package.json", cancellationToken).ConfigureAwait(false) : null;
        var isMonorepo = (rootManifest?.HasWorkspaces ?? false) || context.Exists("pnpm-workspace.yaml") || context.Exists("lerna.json");
        if (isMonorepo)
        {
            context.IsMonorepo = true;
        }

        var rootPackageManager = DetectPackageManager(context, string.Empty, rootManifest);
        if (rootManifest is not null)
        {
            Contribute(context, rootManifest, rootPackageManager, includeInstall: true);
        }

        // Workspace packages, or sub-projects (e.g. "ClientApp/") when the root has no package.json.
        if (rootManifest is not null && !isMonorepo)
        {
            return;
        }

        var nested = manifests
            .Where(p => !RelativePaths.IsAtRoot(p) && RelativePaths.Depth(p) <= MaxNestedDepth)
            .Take(MaxNestedPackages)
            .ToList();
        foreach (var path in nested)
        {
            var manifest = await ReadAsync(context, path, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            var packageManager = isMonorepo ? rootPackageManager : DetectPackageManager(context, manifest.Directory, manifest);
            Contribute(context, manifest, packageManager, includeInstall: !isMonorepo);
        }
    }

    private static void Contribute(DetectionContext context, PackageManifest manifest, string packageManager, bool includeInstall)
    {
        context.AddBuildSystem(packageManager);
        context.AddTechnology(packageManager, TechnologyKind.PackageManager, manifest.Path);

        foreach (var dependency in manifest.Dependencies)
        {
            foreach (var known in KnownPackages)
            {
                var matches = known.IsPrefix
                    ? dependency.StartsWith(known.Package, StringComparison.OrdinalIgnoreCase)
                    : dependency.Equals(known.Package, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                {
                    continue;
                }

                context.AddTechnology(known.Name, known.Kind, $"{dependency} in {manifest.Path}");
                if (known.IsTest)
                {
                    context.AddTestFramework(known.Name);
                }
            }
        }

        var directory = manifest.Directory;
        var idPrefix = directory.Length == 0 ? packageManager : $"{packageManager}:{directory}";
        var nameSuffix = directory.Length == 0 ? string.Empty : $" ({directory})";

        if (includeInstall)
        {
            context.AddCommand(new DetectedCommand
            {
                Id = $"{idPrefix}:install",
                Name = "Install dependencies" + nameSuffix,
                CommandLine = $"{packageManager} install",
                Category = CommandCategory.Install,
                Source = manifest.Path,
                WorkingDirectory = directory,
            });
        }

        var scriptNames = manifest.Scripts.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (script, body) in manifest.Scripts)
        {
            if (IsAutomaticHook(script, scriptNames))
            {
                continue;
            }

            context.AddCommand(new DetectedCommand
            {
                Id = $"{idPrefix}:{script}",
                Name = script + nameSuffix,
                CommandLine = RunScript(packageManager, script),
                Category = CommandCategorizer.FromName(script),
                Source = manifest.Path,
                WorkingDirectory = directory,
                Description = body.Length > 200 ? body[..200] + "…" : body,
            });
        }
    }

    private static bool IsAutomaticHook(string script, HashSet<string> allScripts)
    {
        if (LifecycleScripts.Contains(script))
        {
            return true;
        }

        // "prebuild"/"postbuild" run automatically around "build".
        foreach (var prefix in new[] { "pre", "post" })
        {
            if (script.Length > prefix.Length && script.StartsWith(prefix, StringComparison.Ordinal) && allScripts.Contains(script[prefix.Length..]))
            {
                return true;
            }
        }

        return false;
    }

    private static string RunScript(string packageManager, string script)
    {
        var quoted = CommandLines.Quote(script);

        // "bun test" would start Bun's own test runner instead of the "test" script.
        if (script == "test" && packageManager != "bun")
        {
            return $"{packageManager} test";
        }

        return $"{packageManager} run {quoted}";
    }

    internal static string DetectPackageManager(DetectionContext context, string directory, PackageManifest? manifest)
    {
        if (context.Exists(RelativePaths.Combine(directory, "pnpm-lock.yaml")))
        {
            return "pnpm";
        }

        if (context.Exists(RelativePaths.Combine(directory, "yarn.lock")))
        {
            return "yarn";
        }

        if (context.Exists(RelativePaths.Combine(directory, "bun.lockb")) || context.Exists(RelativePaths.Combine(directory, "bun.lock")))
        {
            return "bun";
        }

        if (context.Exists(RelativePaths.Combine(directory, "package-lock.json")))
        {
            return "npm";
        }

        // "packageManager": "pnpm@9.1.0" (Corepack).
        var declared = manifest?.PackageManager?.Split('@', 2)[0].Trim().ToLowerInvariant();
        return declared is "npm" or "pnpm" or "yarn" or "bun" ? declared : "npm";
    }

    private static async Task<PackageManifest?> ReadAsync(DetectionContext context, string path, CancellationToken cancellationToken)
    {
        var text = await context.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return null;
        }

        using var document = JsonLite.TryParse(text, out var error);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            context.Notes.Add($"{path} could not be read ({error ?? "not a JSON object"}); its scripts are not listed.");
            return null;
        }

        var root = document.RootElement;
        var dependencies = new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" }
            .SelectMany(section => root.PropertyNames(section))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hasWorkspaces = root.TryGetProperty("workspaces", out var workspaces)
            && (workspaces.ValueKind == JsonValueKind.Array
                || (workspaces.ValueKind == JsonValueKind.Object && workspaces.GetArray("packages") is not null));

        return new PackageManifest(path, RelativePaths.DirectoryOf(path), root.StringMap("scripts"), dependencies, root.GetString("packageManager"), hasWorkspaces);
    }

    internal sealed record PackageManifest(
        string Path,
        string Directory,
        IReadOnlyList<KeyValuePair<string, string>> Scripts,
        IReadOnlyList<string> Dependencies,
        string? PackageManager,
        bool HasWorkspaces);
}
