namespace ForgeDesk.Core.Detection;

public enum CommandCategory
{
    Other = 0,
    Dev,
    Build,
    Test,
    Lint,
    Format,
    Package,
    Install,
    Clean,
    Run,
    Deploy,
}

/// <summary>A command ForgeDesk can run for the project (detected or user-defined).</summary>
public sealed record DetectedCommand
{
    /// <summary>Stable id, e.g. "npm:build" or "custom:0192…". Used to link runs to commands.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Shell command line, run from <see cref="WorkingDirectory"/>.</summary>
    public required string CommandLine { get; init; }

    public CommandCategory Category { get; init; }

    /// <summary>Where it came from ("package.json", "Cargo.toml", "Makefile", "custom"…).</summary>
    public required string Source { get; init; }

    /// <summary>Directory relative to the project root ("" = root), forward slashes.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    public string? Description { get; init; }

    public bool IsCustom { get; init; }
}

public sealed record LanguageShare(string Language, int Files, long Bytes, double Percentage, string Color);

public enum TechnologyKind
{
    Language,
    Framework,
    Library,
    BuildTool,
    PackageManager,
    TestFramework,
    Runtime,
    Tooling,
    Infrastructure,
}

public sealed record Technology(string Name, TechnologyKind Kind, string Evidence);

public sealed record TestInfo(bool HasTests, IReadOnlyList<string> Frameworks, IReadOnlyList<string> Locations);

/// <summary>A GitHub Actions workflow file found under .github/workflows.</summary>
public sealed record WorkflowFile(string Name, string RelativePath, IReadOnlyList<string> Triggers);

public enum StructureKind
{
    Source,
    Tests,
    Docs,
    Config,
    Build,
    Assets,
    Scripts,
    Dependencies,
    Ci,
    Other,
}

public sealed record StructureEntry(string Name, bool IsDirectory, StructureKind Kind, string? Note = null);

/// <summary>Everything ForgeDesk understood about a project folder.</summary>
public sealed record ProjectProfile
{
    public DateTimeOffset DetectedAt { get; init; }
    public string? PrimaryLanguage { get; init; }
    public IReadOnlyList<LanguageShare> Languages { get; init; } = [];
    public IReadOnlyList<Technology> Technologies { get; init; } = [];

    /// <summary>Build systems / toolchains, e.g. "npm", "Cargo", ".NET SDK", "Gradle".</summary>
    public IReadOnlyList<string> BuildSystems { get; init; } = [];

    public IReadOnlyList<DetectedCommand> Commands { get; init; } = [];
    public TestInfo Tests { get; init; } = new(false, [], []);
    public IReadOnlyList<WorkflowFile> Workflows { get; init; } = [];
    public IReadOnlyList<StructureEntry> Structure { get; init; } = [];

    /// <summary>Well-known files that exist (README.md, LICENSE, .editorconfig…), relative paths.</summary>
    public IReadOnlyList<string> ImportantFiles { get; init; } = [];

    public int FileCount { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>True when the scan stopped early because the project is huge.</summary>
    public bool ScanTruncated { get; init; }

    public bool IsMonorepo { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public IEnumerable<DetectedCommand> CommandsIn(CommandCategory category) => Commands.Where(c => c.Category == category);
}
