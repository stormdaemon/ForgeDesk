using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Rust: Cargo.toml (packages and workspaces), cargo commands, well-known crates.</summary>
internal sealed class RustDetector : IEcosystemDetector
{
    public const int MaxMemberManifests = 20;
    public const int MaxRunnableMembers = 4;

    private static readonly (string Crate, string Name, TechnologyKind Kind)[] KnownCrates =
    [
        ("tokio", "Tokio", TechnologyKind.Runtime),
        ("actix-web", "Actix Web", TechnologyKind.Framework),
        ("axum", "Axum", TechnologyKind.Framework),
        ("tauri", "Tauri", TechnologyKind.Framework),
        ("rocket", "Rocket", TechnologyKind.Framework),
        ("warp", "Warp", TechnologyKind.Framework),
        ("bevy", "Bevy", TechnologyKind.Framework),
    ];

    public string Name => "Rust";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        if (!context.Exists("Cargo.toml"))
        {
            return;
        }

        var rootText = await context.ReadTextAsync("Cargo.toml", cancellationToken).ConfigureAwait(false);
        if (rootText is null)
        {
            return;
        }

        var root = TomlLite.Parse(rootText);
        context.AddTechnology("Rust", TechnologyKind.Language, "Cargo.toml");
        context.AddTechnology("Cargo", TechnologyKind.BuildTool, "Cargo.toml");
        context.AddBuildSystem("Cargo");

        var isWorkspace = root.HasTable("workspace");
        var members = new List<(string Directory, TomlLite Manifest)>();
        if (isWorkspace)
        {
            foreach (var path in context.FilesNamed("Cargo.toml").Where(p => !RelativePaths.IsAtRoot(p) && RelativePaths.Depth(p) <= 3)
                         .OrderBy(p => p, StringComparer.Ordinal).Take(MaxMemberManifests))
            {
                var text = await context.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (text is not null)
                {
                    members.Add((RelativePaths.DirectoryOf(path), TomlLite.Parse(text)));
                }
            }

            var declaredMembers = root.GetStringArray("workspace", "members").Count;
            if (Math.Max(declaredMembers, members.Count) > 1)
            {
                context.IsMonorepo = true;
            }
        }

        foreach (var (manifest, source) in members.Select(m => (m.Manifest, RelativePaths.Combine(m.Directory, "Cargo.toml"))).Prepend((root, "Cargo.toml")))
        {
            foreach (var crate in Dependencies(manifest))
            {
                foreach (var known in KnownCrates.Where(k => k.Crate.Equals(crate, StringComparison.OrdinalIgnoreCase)))
                {
                    context.AddTechnology(known.Name, known.Kind, $"{crate} in {source}");
                }
            }
        }

        AddCommands(context, root, isWorkspace, members);

        context.AddTestFramework("cargo test");
        foreach (var testsDirectory in members.Select(m => RelativePaths.Combine(m.Directory, "tests")).Prepend("tests"))
        {
            if (context.HasDirectory(testsDirectory))
            {
                context.TestLocations.Add(testsDirectory);
            }
        }
    }

    private static void AddCommands(DetectionContext context, TomlLite root, bool isWorkspace, List<(string Directory, TomlLite Manifest)> members)
    {
        var all = isWorkspace ? " --workspace" : string.Empty;
        Add(context, "build", "Build", $"cargo build{all}", CommandCategory.Build);
        Add(context, "build-release", "Build release", $"cargo build --release{all}", CommandCategory.Package);
        Add(context, "test", "Test", $"cargo test{all}", CommandCategory.Test);
        Add(context, "check", "Check", $"cargo check{all}", CommandCategory.Lint);
        Add(context, "clippy", "Clippy", $"cargo clippy{all}", CommandCategory.Lint);
        Add(context, "fmt", "Format", isWorkspace ? "cargo fmt --all" : "cargo fmt", CommandCategory.Format);
        Add(context, "clean", "Clean", "cargo clean", CommandCategory.Clean);

        if (context.HasDirectory("benches") || root.HasTable("bench"))
        {
            Add(context, "bench", "Benchmarks", $"cargo bench{all}", CommandCategory.Test);
        }

        if (root.HasTable("package") && (context.Exists("src/main.rs") || root.HasTable("bin")))
        {
            Add(context, "run", "Run", "cargo run", CommandCategory.Run);
        }
        else
        {
            var binaries = members
                .Where(m => context.Exists(RelativePaths.Combine(m.Directory, "src/main.rs")) || m.Manifest.HasTable("bin"))
                .Select(m => m.Manifest.GetString("package", "name"))
                .OfType<string>()
                .Take(MaxRunnableMembers);
            foreach (var binary in binaries)
            {
                Add(context, $"run:{binary}", $"Run {binary}", $"cargo run -p {CommandLines.Quote(binary)}", CommandCategory.Run);
            }
        }
    }

    private static IEnumerable<string> Dependencies(TomlLite manifest)
    {
        foreach (var table in manifest.Tables)
        {
            var segments = table.Split('.');
            var last = segments[^1];
            if (last is "dependencies" or "dev-dependencies" or "build-dependencies")
            {
                foreach (var key in manifest.KeysOf(table))
                {
                    yield return key;
                }
            }
            else if (segments.Length >= 2 && segments[^2] is "dependencies" or "dev-dependencies" or "build-dependencies")
            {
                // [dependencies.tokio] style.
                yield return last;
            }
        }
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category) =>
        context.AddCommand(new DetectedCommand { Id = $"cargo:{id}", Name = name, CommandLine = commandLine, Category = category, Source = "Cargo.toml" });
}
