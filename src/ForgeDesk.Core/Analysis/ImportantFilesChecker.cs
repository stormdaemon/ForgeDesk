namespace ForgeDesk.Core.Analysis;

internal enum ImportantFileKind
{
    Readme,
    License,
    GitIgnore,
    CiWorkflow,
    Tests,
    Contributing,
    Changelog,
    Security,
    EditorConfig,
    Lockfile,
}

/// <summary>A checklist row plus what it is about, so scoring does not depend on wording.</summary>
internal sealed record ImportantFileFinding(ImportantFileKind Kind, ImportantFileCheck Check, string? Ecosystem = null);

/// <summary>The files a healthy project usually has, each with a short reason.</summary>
internal static class ImportantFilesChecker
{
    private static readonly string[] CiFiles =
    [
        ".gitlab-ci.yml", "azure-pipelines.yml", ".circleci/config.yml", "Jenkinsfile", ".travis.yml", "appveyor.yml",
        "bitbucket-pipelines.yml", ".drone.yml",
    ];

    // Manifest at the root → lockfiles its package manager writes.
    private static readonly (string Manifest, string Ecosystem, string[] Lockfiles)[] PackageManagers =
    [
        ("package.json", "npm", ["package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lockb", "bun.lock", "npm-shrinkwrap.json"]),
        ("Cargo.toml", "Cargo", ["Cargo.lock"]),
        ("composer.json", "Composer", ["composer.lock"]),
        ("Gemfile", "Bundler", ["Gemfile.lock"]),
        ("Pipfile", "Pipenv", ["Pipfile.lock"]),
        ("go.mod", "Go", ["go.sum"]),
    ];

    /// <param name="files">Relative paths of the project files.</param>
    /// <param name="hasTests">Whether tests were found (detector or naming conventions).</param>
    /// <param name="testLocation">Where the tests live, for the checklist link.</param>
    /// <param name="ecosystemsWithDependencies">Ecosystems with at least one declared dependency (go.mod without requires needs no go.sum).</param>
    public static IReadOnlyList<ImportantFileFinding> Check(
        IReadOnlyCollection<string> files, bool hasTests, string? testLocation, IReadOnlySet<string> ecosystemsWithDependencies)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ecosystemsWithDependencies);
        var rootFiles = files.Where(f => !f.Contains('/', StringComparison.Ordinal)).ToList();
        var findings = new List<ImportantFileFinding>
        {
            Find(ImportantFileKind.Readme, "README", "Explains what the project does and how to build and run it.",
                RootOrDocs(files, name => StemIs(name, "README"))),
            Find(ImportantFileKind.License, "LICENSE", "Without a license, nobody else may legally use, change or share the code.",
                rootFiles.FirstOrDefault(name => StemStartsWith(name, "LICENSE") || StemStartsWith(name, "LICENCE") || StemIs(name, "COPYING") || StemIs(name, "UNLICENSE"))),
            Find(ImportantFileKind.GitIgnore, ".gitignore", "Keeps build output, dependencies and secrets out of Git.",
                rootFiles.FirstOrDefault(name => name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase))),
            Find(ImportantFileKind.CiWorkflow, "CI workflow", "Builds and tests every push automatically, so breakages are caught early.",
                FindCi(files)),
            new(ImportantFileKind.Tests, new ImportantFileCheck("Tests", hasTests, hasTests ? testLocation : null,
                "Automated tests catch regressions before your users do.")),
            Find(ImportantFileKind.Contributing, "CONTRIBUTING", "Tells contributors how to set up the project and submit changes.",
                RootOrDocs(files, name => StemIs(name, "CONTRIBUTING"))),
            Find(ImportantFileKind.Changelog, "CHANGELOG", "Lets users see what changed between versions.",
                rootFiles.FirstOrDefault(name => StemIs(name, "CHANGELOG") || StemIs(name, "CHANGES") || StemIs(name, "HISTORY") || StemIs(name, "RELEASE_NOTES") || StemIs(name, "RELEASENOTES"))),
            Find(ImportantFileKind.Security, "SECURITY", "Tells people how to report vulnerabilities privately.",
                RootOrDocs(files, name => StemIs(name, "SECURITY"))),
            Find(ImportantFileKind.EditorConfig, ".editorconfig", "Keeps indentation and line endings consistent across editors.",
                rootFiles.FirstOrDefault(name => name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase))),
        };

        foreach (var (manifest, ecosystem, lockfiles) in PackageManagers)
        {
            if (!rootFiles.Contains(manifest, StringComparer.OrdinalIgnoreCase)
                || (ecosystem == "Go" && !ecosystemsWithDependencies.Contains("Go")))
            {
                continue;
            }

            var lockfile = rootFiles.FirstOrDefault(name => lockfiles.Contains(name, StringComparer.OrdinalIgnoreCase));
            findings.Add(new ImportantFileFinding(ImportantFileKind.Lockfile,
                new ImportantFileCheck($"Lockfile ({ecosystem})", lockfile is not null, lockfile,
                    "Pins exact dependency versions so every install gets the same packages."),
                ecosystem));
        }

        return findings;
    }

    private static ImportantFileFinding Find(ImportantFileKind kind, string label, string why, string? path) =>
        new(kind, new ImportantFileCheck(label, path is not null, path, why));

    /// <summary>At the root, or in .github/ or docs/ where GitHub also looks.</summary>
    private static string? RootOrDocs(IReadOnlyCollection<string> files, Func<string, bool> nameMatches)
    {
        string? best = null;
        foreach (var file in files)
        {
            var slash = file.LastIndexOf('/');
            var folder = slash < 0 ? string.Empty : file[..slash];
            if (!nameMatches(file[(slash + 1)..]))
            {
                continue;
            }

            if (folder.Length == 0)
            {
                return file;
            }

            if (best is null && (folder.Equals(".github", StringComparison.OrdinalIgnoreCase) || folder.Equals("docs", StringComparison.OrdinalIgnoreCase)))
            {
                best = file;
            }
        }

        return best;
    }

    private static string? FindCi(IReadOnlyCollection<string> files)
    {
        var workflow = files
            .Where(f => f.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
                && (f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return workflow ?? files.FirstOrDefault(f => CiFiles.Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    private static bool StemIs(string fileName, string stem)
    {
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        var actual = dot > 0 ? fileName[..dot] : fileName;
        return actual.Equals(stem, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StemStartsWith(string fileName, string prefix) =>
        fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
