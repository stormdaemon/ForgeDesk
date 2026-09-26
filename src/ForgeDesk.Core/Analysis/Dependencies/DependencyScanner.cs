using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>
/// Finds dependency manifests among the project files and runs the matching parser on each,
/// shallowest manifests first, within <see cref="AnalysisLimits"/> bounds.
/// </summary>
internal static class DependencyScanner
{
    private const string CentralPackagesFile = "Directory.Packages.props";
    private const string VersionCatalogFile = "libs.versions.toml";

    private enum ManifestKind
    {
        Npm,
        Cargo,
        GoMod,
        PipRequirements,
        PyProject,
        MsBuild,
        Maven,
        Gradle,
        Composer,
        Gemfile,
    }

    public static async Task<IReadOnlyList<DependencyInfo>> ScanAsync(string root, IEnumerable<string> relativeFiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relativeFiles);
        var candidates = relativeFiles
            .Select(path => (Path: path, Kind: Classify(FileName(path))))
            .Where(c => c.Kind is not null || IsSupportFile(FileName(c.Path)))
            .OrderBy(c => c.Path.Count(ch => ch == '/'))
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Take(AnalysisLimits.MaxManifests)
            .ToList();

        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, _) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReadAsync(root, path, cancellationToken).ConfigureAwait(false) is { } text)
            {
                contents[path] = text;
            }
        }

        var centralVersions = SupportFiles(contents, CentralPackagesFile, MsBuildDependencyParser.ReadCentralVersions);
        var catalogs = SupportFiles(contents, VersionCatalogFile, GradleVersionCatalog.Parse);
        var cargoWorkspaceVersions = contents
            .Where(c => FileName(c.Key).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
            .Select(c => CargoDependencyParser.ReadWorkspaceVersions(c.Value))
            .FirstOrDefault(v => v.Count > 0);

        var dependencies = new List<DependencyInfo>();
        foreach (var (path, kind) in candidates)
        {
            if (kind is null || !contents.TryGetValue(path, out var text))
            {
                continue;
            }

            var parsed = kind switch
            {
                ManifestKind.Npm => NpmDependencyParser.Parse(text, path),
                ManifestKind.Cargo => CargoDependencyParser.Parse(text, path, cargoWorkspaceVersions),
                ManifestKind.GoMod => GoModDependencyParser.Parse(text, path),
                ManifestKind.PipRequirements => PipRequirementsParser.Parse(text, path),
                ManifestKind.PyProject => PyProjectDependencyParser.Parse(text, path),
                ManifestKind.MsBuild => MsBuildDependencyParser.Parse(text, path, Nearest(centralVersions, path)),
                ManifestKind.Maven => MavenDependencyParser.Parse(text, path),
                ManifestKind.Gradle => GradleDependencyParser.Parse(text, path, NearestCatalog(catalogs, path)),
                ManifestKind.Composer => ComposerDependencyParser.Parse(text, path),
                ManifestKind.Gemfile => GemfileDependencyParser.Parse(text, path),
                _ => [],
            };

            foreach (var dependency in parsed.DistinctBy(d => (d.Name, d.IsDevelopment)))
            {
                if (dependencies.Count >= AnalysisLimits.MaxDependencies)
                {
                    return dependencies;
                }

                dependencies.Add(dependency);
            }
        }

        return dependencies;
    }

    private static ManifestKind? Classify(string fileName)
    {
        if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.Npm;
        }

        if (fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.Cargo;
        }

        if (fileName.Equals("go.mod", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.GoMod;
        }

        if (fileName.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.PipRequirements;
        }

        if (fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.PyProject;
        }

        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.MsBuild;
        }

        if (fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.Maven;
        }

        if (fileName.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) || fileName.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.Gradle;
        }

        if (fileName.Equals("composer.json", StringComparison.OrdinalIgnoreCase))
        {
            return ManifestKind.Composer;
        }

        return fileName.Equals("Gemfile", StringComparison.OrdinalIgnoreCase) ? ManifestKind.Gemfile : null;
    }

    private static bool IsSupportFile(string fileName) =>
        fileName.Equals(CentralPackagesFile, StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(VersionCatalogFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parsed support files keyed by their folder ("" for the root).</summary>
    private static Dictionary<string, T> SupportFiles<T>(Dictionary<string, string> contents, string fileName, Func<string, T> parse) =>
        contents
            .Where(c => FileName(c.Key).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(c => Folder(c.Key), c => parse(c.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>MSBuild uses the Directory.Packages.props of the closest parent folder.</summary>
    private static T? Nearest<T>(Dictionary<string, T> byFolder, string manifestPath)
        where T : class
    {
        for (var folder = Folder(manifestPath); ; folder = Folder(folder))
        {
            if (byFolder.TryGetValue(folder, out var value))
            {
                return value;
            }

            if (folder.Length == 0)
            {
                return null;
            }
        }
    }

    /// <summary>Gradle catalogs live in a gradle/ folder next to the build root.</summary>
    private static GradleVersionCatalog? NearestCatalog(Dictionary<string, GradleVersionCatalog> byFolder, string manifestPath)
    {
        for (var folder = Folder(manifestPath); ; folder = Folder(folder))
        {
            if (byFolder.TryGetValue(folder.Length == 0 ? "gradle" : folder + "/gradle", out var catalog))
            {
                return catalog;
            }

            if (folder.Length == 0)
            {
                return null;
            }
        }
    }

    private static async Task<string?> ReadAsync(string root, string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            var full = Path.Combine(root, PathUtil.ToPlatform(relativePath));
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > AnalysisLimits.MaxManifestBytes)
            {
                return null;
            }

            return await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FileName(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    private static string Folder(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }
}
