using System.Collections.Frozen;

namespace ForgeDesk.Core.Detection;

/// <summary>
/// Language breakdown by bytes, in the spirit of GitHub's language bar: vendored, generated,
/// minified, documentation and lock files are ignored, and data/prose formats (JSON, YAML,
/// Markdown…) only count when a project has no code at all.
/// </summary>
internal static class LanguageStatistics
{
    /// <summary>Languages listed individually; the remainder is merged into "Other".</summary>
    public const int MaxLanguages = 7;

    public const string OtherLanguage = "Other";

    private static readonly FrozenSet<string> VendoredFolders = new[]
    {
        "vendor", "vendors", "third_party", "thirdparty", "third-party", "3rdparty", "external", "extern",
        "node_modules", "bower_components", "Pods", "Carthage", ".yarn", "dist", "deps", "docs", "doc", "Documentation",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> LockFiles = new[]
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock", "Cargo.lock",
        "poetry.lock", "Pipfile.lock", "uv.lock", "pdm.lock", "composer.lock", "Gemfile.lock", "go.sum", "go.work.sum",
        "packages.lock.json", "project.assets.json", "flake.lock", "pubspec.lock", "mix.lock", "gradle.lockfile",
        "Package.resolved", "Podfile.lock", "deno.lock",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] GeneratedSuffixes =
    [
        ".min.js", ".min.mjs", ".min.css", "-min.js", ".bundle.js", ".map",
        ".designer.cs", ".designer.vb", ".g.cs", ".g.i.cs", ".generated.cs", ".generated.ts", ".generated.js",
        ".pb.go", "_pb2.py", "_pb2_grpc.py", ".pb.cc", ".pb.h", "ModelSnapshot.cs", ".AssemblyInfo.cs",
    ];

    public static IReadOnlyList<LanguageShare> Compute(IEnumerable<ScannedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var totals = new Dictionary<string, (LanguageDefinition Definition, int Files, long Bytes)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (IsExcluded(file.RelativePath) || LanguageCatalog.Find(file.RelativePath) is not { } definition)
            {
                continue;
            }

            var current = totals.TryGetValue(definition.Name, out var existing) ? existing : (definition, 0, 0L);
            totals[definition.Name] = (definition, current.Item2 + 1, current.Item3 + file.Length);
        }

        var code = totals.Values.Where(t => t.Definition.Type is LanguageType.Programming or LanguageType.Markup).ToList();
        var pool = code.Count > 0 ? code : totals.Values.ToList();
        if (pool.Count == 0)
        {
            return [];
        }

        // Empty files carry no bytes: fall back to file counts so the breakdown stays meaningful.
        var byBytes = pool.Sum(t => t.Bytes) > 0;
        double Weight((LanguageDefinition Definition, int Files, long Bytes) t) => byBytes ? t.Bytes : t.Files;
        var total = pool.Sum(Weight);

        var ordered = pool
            .OrderByDescending(Weight)
            .ThenByDescending(t => t.Files)
            .ThenBy(t => t.Definition.Name, StringComparer.Ordinal)
            .ToList();

        var shares = ordered
            .Take(MaxLanguages)
            .Select(t => new LanguageShare(t.Definition.Name, t.Files, t.Bytes, Percent(Weight(t), total), t.Definition.Color))
            .ToList();

        var rest = ordered.Skip(MaxLanguages).ToList();
        if (rest.Count > 0)
        {
            shares.Add(new LanguageShare(OtherLanguage, rest.Sum(t => t.Files), rest.Sum(t => t.Bytes), Percent(rest.Sum(Weight), total), LanguageCatalog.OtherColor));
        }

        return shares;
    }

    /// <summary>Vendored, documentation, generated, minified and lock files do not count as the project's code.</summary>
    public static bool IsExcluded(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (VendoredFolders.Contains(segments[i]) || HeavyFolders.IsDependencyOrCache(segments[i]))
            {
                return true;
            }

            // ASP.NET client libraries restored by LibMan.
            if (i + 1 < segments.Length - 1 && segments[i].Equals("wwwroot", StringComparison.OrdinalIgnoreCase)
                && segments[i + 1].Equals("lib", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var fileName = segments[^1];
        return LockFiles.Contains(fileName)
            || GeneratedSuffixes.Any(s => fileName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLockFile(string fileName) => LockFiles.Contains(fileName);

    private static double Percent(double part, double total) => total <= 0 ? 0 : Math.Round(part * 100 / total, 1);
}
