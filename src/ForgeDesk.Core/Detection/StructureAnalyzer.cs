using System.Collections.Frozen;

namespace ForgeDesk.Core.Detection;

/// <summary>Describes the top level of a project folder: what each entry is for, in a few words.</summary>
internal static class StructureAnalyzer
{
    public const int MaxEntries = 80;

    private static readonly EnumerationOptions ListingOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    private static readonly FrozenDictionary<string, (StructureKind Kind, string Note)> KnownDirectories = new Dictionary<string, (StructureKind, string)>
    {
        ["src"] = (StructureKind.Source, "Source code"),
        ["source"] = (StructureKind.Source, "Source code"),
        ["sources"] = (StructureKind.Source, "Source code"),
        ["lib"] = (StructureKind.Source, "Library code"),
        ["libs"] = (StructureKind.Source, "Libraries"),
        ["app"] = (StructureKind.Source, "Application code"),
        ["apps"] = (StructureKind.Source, "Applications"),
        ["pkg"] = (StructureKind.Source, "Go packages"),
        ["cmd"] = (StructureKind.Source, "Command entry points"),
        ["internal"] = (StructureKind.Source, "Internal packages"),
        ["crates"] = (StructureKind.Source, "Rust crates"),
        ["modules"] = (StructureKind.Source, "Modules"),
        ["components"] = (StructureKind.Source, "UI components"),
        ["core"] = (StructureKind.Source, "Core code"),
        ["server"] = (StructureKind.Source, "Server code"),
        ["client"] = (StructureKind.Source, "Client code"),
        ["web"] = (StructureKind.Source, "Web front end"),
        ["frontend"] = (StructureKind.Source, "Front end"),
        ["backend"] = (StructureKind.Source, "Back end"),
        ["api"] = (StructureKind.Source, "API"),
        ["services"] = (StructureKind.Source, "Services"),
        ["include"] = (StructureKind.Source, "Public headers"),
        ["test"] = (StructureKind.Tests, "Tests"),
        ["tests"] = (StructureKind.Tests, "Tests"),
        ["spec"] = (StructureKind.Tests, "Specs"),
        ["specs"] = (StructureKind.Tests, "Specs"),
        ["__tests__"] = (StructureKind.Tests, "Tests"),
        ["e2e"] = (StructureKind.Tests, "End-to-end tests"),
        ["testing"] = (StructureKind.Tests, "Tests"),
        ["cypress"] = (StructureKind.Tests, "Cypress tests"),
        ["playwright"] = (StructureKind.Tests, "Playwright tests"),
        ["benches"] = (StructureKind.Tests, "Benchmarks"),
        ["benchmarks"] = (StructureKind.Tests, "Benchmarks"),
        ["docs"] = (StructureKind.Docs, "Documentation"),
        ["doc"] = (StructureKind.Docs, "Documentation"),
        ["documentation"] = (StructureKind.Docs, "Documentation"),
        ["wiki"] = (StructureKind.Docs, "Wiki"),
        ["guides"] = (StructureKind.Docs, "Guides"),
        ["examples"] = (StructureKind.Docs, "Examples"),
        ["samples"] = (StructureKind.Docs, "Samples"),
        ["config"] = (StructureKind.Config, "Configuration"),
        ["configs"] = (StructureKind.Config, "Configuration"),
        ["conf"] = (StructureKind.Config, "Configuration"),
        ["settings"] = (StructureKind.Config, "Settings"),
        [".config"] = (StructureKind.Config, "Tool configuration"),
        [".vscode"] = (StructureKind.Config, "VS Code settings"),
        [".devcontainer"] = (StructureKind.Config, "Dev container"),
        [".husky"] = (StructureKind.Config, "Git hooks"),
        [".cargo"] = (StructureKind.Config, "Cargo settings"),
        [".mvn"] = (StructureKind.Config, "Maven wrapper"),
        [".idea"] = (StructureKind.Config, "JetBrains IDE settings (not scanned)"),
        [".vs"] = (StructureKind.Config, "Visual Studio cache (not scanned)"),
        ["eng"] = (StructureKind.Build, "Engineering scripts"),
        ["obj"] = (StructureKind.Build, "Intermediate build output (not scanned)"),
        ["target"] = (StructureKind.Build, "Build output (not scanned)"),
        ["dist"] = (StructureKind.Build, "Distribution output (not scanned)"),
        ["out"] = (StructureKind.Build, "Build output (not scanned)"),
        ["__pycache__"] = (StructureKind.Build, "Python cache (not scanned)"),
        [".next"] = (StructureKind.Build, "Next.js build output (not scanned)"),
        [".nuxt"] = (StructureKind.Build, "Nuxt build output (not scanned)"),
        [".gradle"] = (StructureKind.Build, "Gradle cache (not scanned)"),
        ["scripts"] = (StructureKind.Scripts, "Scripts"),
        ["script"] = (StructureKind.Scripts, "Scripts"),
        ["tools"] = (StructureKind.Scripts, "Tools"),
        ["tasks"] = (StructureKind.Scripts, "Tasks"),
        ["hack"] = (StructureKind.Scripts, "Developer scripts"),
        ["node_modules"] = (StructureKind.Dependencies, "npm packages (not scanned)"),
        ["vendor"] = (StructureKind.Dependencies, "Vendored dependencies"),
        [".venv"] = (StructureKind.Dependencies, "Python virtual environment (not scanned)"),
        ["venv"] = (StructureKind.Dependencies, "Python virtual environment (not scanned)"),
        ["bower_components"] = (StructureKind.Dependencies, "Bower packages (not scanned)"),
        ["third_party"] = (StructureKind.Dependencies, "Third-party code"),
        ["external"] = (StructureKind.Dependencies, "External code"),
        ["Pods"] = (StructureKind.Dependencies, "CocoaPods (not scanned)"),
        [".github"] = (StructureKind.Ci, "GitHub workflows and templates"),
        [".circleci"] = (StructureKind.Ci, "CircleCI configuration"),
        [".gitlab"] = (StructureKind.Ci, "GitLab configuration"),
        [".buildkite"] = (StructureKind.Ci, "Buildkite pipelines"),
        [".azure-pipelines"] = (StructureKind.Ci, "Azure Pipelines"),
        ["assets"] = (StructureKind.Assets, "Assets"),
        ["static"] = (StructureKind.Assets, "Static files"),
        ["public"] = (StructureKind.Assets, "Public files"),
        ["images"] = (StructureKind.Assets, "Images"),
        ["img"] = (StructureKind.Assets, "Images"),
        ["media"] = (StructureKind.Assets, "Media"),
        ["resources"] = (StructureKind.Assets, "Resources"),
        ["res"] = (StructureKind.Assets, "Resources"),
        ["fonts"] = (StructureKind.Assets, "Fonts"),
        ["icons"] = (StructureKind.Assets, "Icons"),
        ["wwwroot"] = (StructureKind.Assets, "Web root"),
        ["locales"] = (StructureKind.Assets, "Translations"),
        ["i18n"] = (StructureKind.Assets, "Translations"),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, (StructureKind Kind, string Note)> KnownFiles = new Dictionary<string, (StructureKind, string)>
    {
        ["package.json"] = (StructureKind.Build, "npm manifest"),
        ["Cargo.toml"] = (StructureKind.Build, "Cargo manifest"),
        ["go.mod"] = (StructureKind.Build, "Go module"),
        ["go.work"] = (StructureKind.Build, "Go workspace"),
        ["pyproject.toml"] = (StructureKind.Build, "Python project"),
        ["setup.py"] = (StructureKind.Build, "Python package setup"),
        ["setup.cfg"] = (StructureKind.Config, "Python package settings"),
        ["Pipfile"] = (StructureKind.Dependencies, "Pipenv dependencies"),
        ["pom.xml"] = (StructureKind.Build, "Maven project"),
        ["build.gradle"] = (StructureKind.Build, "Gradle build"),
        ["build.gradle.kts"] = (StructureKind.Build, "Gradle build"),
        ["settings.gradle"] = (StructureKind.Build, "Gradle settings"),
        ["settings.gradle.kts"] = (StructureKind.Build, "Gradle settings"),
        ["gradlew"] = (StructureKind.Build, "Gradle wrapper"),
        ["gradlew.bat"] = (StructureKind.Build, "Gradle wrapper"),
        ["mvnw"] = (StructureKind.Build, "Maven wrapper"),
        ["mvnw.cmd"] = (StructureKind.Build, "Maven wrapper"),
        ["Makefile"] = (StructureKind.Build, "Make targets"),
        ["GNUmakefile"] = (StructureKind.Build, "Make targets"),
        ["CMakeLists.txt"] = (StructureKind.Build, "CMake project"),
        ["CMakePresets.json"] = (StructureKind.Build, "CMake presets"),
        ["Dockerfile"] = (StructureKind.Build, "Container image"),
        ["Containerfile"] = (StructureKind.Build, "Container image"),
        ["compose.yaml"] = (StructureKind.Config, "Docker Compose services"),
        ["compose.yml"] = (StructureKind.Config, "Docker Compose services"),
        ["docker-compose.yml"] = (StructureKind.Config, "Docker Compose services"),
        ["docker-compose.yaml"] = (StructureKind.Config, "Docker Compose services"),
        ["justfile"] = (StructureKind.Build, "just recipes"),
        [".justfile"] = (StructureKind.Build, "just recipes"),
        ["Taskfile.yml"] = (StructureKind.Build, "Task definitions"),
        ["Taskfile.yaml"] = (StructureKind.Build, "Task definitions"),
        ["composer.json"] = (StructureKind.Build, "Composer manifest"),
        ["Gemfile"] = (StructureKind.Dependencies, "Ruby gems"),
        ["Rakefile"] = (StructureKind.Build, "Rake tasks"),
        ["deno.json"] = (StructureKind.Config, "Deno configuration"),
        ["deno.jsonc"] = (StructureKind.Config, "Deno configuration"),
        ["pubspec.yaml"] = (StructureKind.Build, "Dart package"),
        ["Directory.Build.props"] = (StructureKind.Build, "Shared MSBuild settings"),
        ["Directory.Build.targets"] = (StructureKind.Build, "Shared MSBuild targets"),
        ["Directory.Packages.props"] = (StructureKind.Dependencies, "Central package versions"),
        ["global.json"] = (StructureKind.Config, ".NET SDK version"),
        ["nuget.config"] = (StructureKind.Config, "NuGet feeds"),
        ["tsconfig.json"] = (StructureKind.Config, "TypeScript settings"),
        ["pnpm-workspace.yaml"] = (StructureKind.Config, "pnpm workspace"),
        ["lerna.json"] = (StructureKind.Config, "Lerna monorepo"),
        ["nx.json"] = (StructureKind.Config, "Nx workspace"),
        ["turbo.json"] = (StructureKind.Config, "Turborepo pipeline"),
        [".gitignore"] = (StructureKind.Config, "Git ignore rules"),
        [".gitattributes"] = (StructureKind.Config, "Git attributes"),
        [".gitmodules"] = (StructureKind.Config, "Git submodules"),
        [".editorconfig"] = (StructureKind.Config, "Editor settings"),
        [".npmrc"] = (StructureKind.Config, "npm settings"),
        [".nvmrc"] = (StructureKind.Config, "Node.js version"),
        [".python-version"] = (StructureKind.Config, "Python version"),
        [".tool-versions"] = (StructureKind.Config, "Tool versions"),
        [".gitlab-ci.yml"] = (StructureKind.Ci, "GitLab CI pipeline"),
        ["azure-pipelines.yml"] = (StructureKind.Ci, "Azure Pipelines"),
        [".travis.yml"] = (StructureKind.Ci, "Travis CI"),
        ["appveyor.yml"] = (StructureKind.Ci, "AppVeyor"),
        ["Jenkinsfile"] = (StructureKind.Ci, "Jenkins pipeline"),
        ["bitbucket-pipelines.yml"] = (StructureKind.Ci, "Bitbucket Pipelines"),
        [".drone.yml"] = (StructureKind.Ci, "Drone CI"),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AssetExtensions = [".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".bmp"];
    private static readonly string[] ScriptExtensions = [".ps1", ".sh", ".cmd", ".bat", ".bash"];
    private static readonly string[] ConfigExtensions = [".json", ".jsonc", ".toml", ".yml", ".yaml", ".ini", ".cfg", ".conf", ".xml", ".props", ".targets", ".config", ".env"];

    public static IReadOnlyList<StructureEntry> Describe(string root, DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(root).EnumerateFileSystemInfos("*", ListingOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        return entries
            .Where(e => !e.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) && !IsHiddenSystem(e))
            .Select(e =>
            {
                var isDirectory = e is DirectoryInfo;
                var (kind, note) = isDirectory ? ClassifyDirectory(e.Name, context.HasDirectory(e.Name)) : ClassifyFile(e.Name);
                return new StructureEntry(e.Name, isDirectory, kind, note);
            })
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .ToList();
    }

    /// <summary>Classifies a top-level folder; <paramref name="hasScannedFiles"/> tells whether its content was part of the scan.</summary>
    public static (StructureKind Kind, string? Note) ClassifyDirectory(string name, bool hasScannedFiles)
    {
        // Folder names shared by build output and source: what the scan saw decides.
        if (name.Equals("packages", StringComparison.OrdinalIgnoreCase))
        {
            return hasScannedFiles ? (StructureKind.Source, "Workspace packages") : (StructureKind.Dependencies, "Restored packages (not scanned)");
        }

        if (name.Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            return hasScannedFiles ? (StructureKind.Scripts, "Executable scripts") : (StructureKind.Build, "Build output (not scanned)");
        }

        if (name.Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            return hasScannedFiles ? (StructureKind.Build, "Build and packaging scripts") : (StructureKind.Build, "Build output (not scanned)");
        }

        if (KnownDirectories.TryGetValue(name, out var known))
        {
            return known;
        }

        return (StructureKind.Other, null);
    }

    public static (StructureKind Kind, string? Note) ClassifyFile(string name)
    {
        if (KnownFiles.TryGetValue(name, out var known))
        {
            return known;
        }

        var upper = name.ToUpperInvariant();
        if (upper.StartsWith("README", StringComparison.Ordinal))
        {
            return (StructureKind.Docs, "Project overview");
        }

        if (upper.StartsWith("LICENSE", StringComparison.Ordinal) || upper.StartsWith("LICENCE", StringComparison.Ordinal) || upper.StartsWith("COPYING", StringComparison.Ordinal))
        {
            return (StructureKind.Docs, "License");
        }

        if (upper.StartsWith("CHANGELOG", StringComparison.Ordinal) || upper.StartsWith("CHANGES", StringComparison.Ordinal) || upper.StartsWith("HISTORY", StringComparison.Ordinal))
        {
            return (StructureKind.Docs, "Release history");
        }

        if (upper.StartsWith("CONTRIBUTING", StringComparison.Ordinal))
        {
            return (StructureKind.Docs, "Contribution guide");
        }

        if (upper.StartsWith("CODE_OF_CONDUCT", StringComparison.Ordinal))
        {
            return (StructureKind.Docs, "Code of conduct");
        }

        if (upper.StartsWith("SECURITY", StringComparison.Ordinal))
        {
            return (StructureKind.Docs, "Security policy");
        }

        var extension = Path.GetExtension(name);
        if (upper.EndsWith(".SLN", StringComparison.Ordinal) || upper.EndsWith(".SLNX", StringComparison.Ordinal))
        {
            return (StructureKind.Build, ".NET solution");
        }

        if (upper.EndsWith("PROJ", StringComparison.Ordinal) && extension.Length > 0)
        {
            return (StructureKind.Build, ".NET project");
        }

        if (upper.StartsWith("REQUIREMENTS", StringComparison.Ordinal) && upper.EndsWith(".TXT", StringComparison.Ordinal))
        {
            return (StructureKind.Dependencies, "Python requirements");
        }

        if (LanguageStatistics.IsLockFile(name))
        {
            return (StructureKind.Dependencies, "Lockfile");
        }

        if (upper.StartsWith(".ENV", StringComparison.Ordinal))
        {
            return (StructureKind.Config, "Environment variables");
        }

        if (name.Contains(".config.", StringComparison.OrdinalIgnoreCase) || upper.StartsWith(".ESLINTRC", StringComparison.Ordinal) || upper.StartsWith(".PRETTIERRC", StringComparison.Ordinal))
        {
            return (StructureKind.Config, "Tool configuration");
        }

        if (ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return (StructureKind.Scripts, "Script");
        }

        if (AssetExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return (StructureKind.Assets, "Image");
        }

        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return (StructureKind.Docs, "Documentation");
        }

        if (ConfigExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.'))
        {
            return (StructureKind.Config, null);
        }

        if (LanguageCatalog.Find(name) is { Type: LanguageType.Programming or LanguageType.Markup } language)
        {
            return (StructureKind.Source, $"{language.Name} source");
        }

        return (StructureKind.Other, null);
    }

    private static bool IsHiddenSystem(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.Hidden) != 0 && (entry.Attributes & FileAttributes.System) != 0;
}
