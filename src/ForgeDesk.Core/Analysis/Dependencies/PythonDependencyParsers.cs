using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>A PEP 508 requirement ("requests[socks]>=2.31,<3 ; python_version>'3.8'") reduced to name and version.</summary>
internal static partial class PythonRequirement
{
    public static bool TryParse(string text, out string name, out string? version)
    {
        name = string.Empty;
        version = null;
        var match = Requirement().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        name = match.Groups["name"].Value;
        var spec = match.Groups["spec"].Value.Trim().TrimStart('(').TrimEnd(')').Trim();
        if (spec.StartsWith('@'))
        {
            // "name @ https://…": a direct reference, not a version.
            return true;
        }

        if (spec.StartsWith("==", StringComparison.Ordinal) && !spec.Contains(',', StringComparison.Ordinal))
        {
            spec = spec[2..].Trim();
        }

        version = spec.Length == 0 ? null : spec;
        return true;
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?)\s*(?:\[[^\]]*\])?\s*(?<spec>[^;]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Requirement();
}

/// <summary>requirements*.txt (pip). Files named like requirements-dev.txt hold development dependencies.</summary>
internal static class PipRequirementsParser
{
    public const string Ecosystem = "PyPI";

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        var fileName = Path.GetFileName(manifestPath);
        var isDevelopment = fileName.Contains("dev", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("test", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("lint", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("docs", StringComparison.OrdinalIgnoreCase);

        var result = new List<DependencyInfo>();
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal).Split('\n');
        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();

            // Options (-r, -e, --index-url), paths and bare URLs are not named dependencies.
            if (line.Length == 0
                || line[0] is '-' or '.' or '/'
                || (line.Contains("://", StringComparison.Ordinal) && !line.Contains(" @ ", StringComparison.Ordinal)))
            {
                continue;
            }

            var options = line.IndexOf(" --", StringComparison.Ordinal);
            if (options > 0)
            {
                line = line[..options];
            }

            if (PythonRequirement.TryParse(line, out var name, out var version))
            {
                result.Add(new DependencyInfo(Ecosystem, name, version, isDevelopment, manifestPath));
            }
        }

        return result;
    }

    private static string StripComment(string line)
    {
        if (line.TrimStart().StartsWith('#'))
        {
            return string.Empty;
        }

        var comment = line.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }
}

/// <summary>
/// pyproject.toml: PEP 621 <c>[project] dependencies</c> and optional dependencies, PEP 735
/// <c>[dependency-groups]</c>, and Poetry's <c>[tool.poetry.*dependencies]</c> tables.
/// </summary>
internal static class PyProjectDependencyParser
{
    public const string Ecosystem = PipRequirementsParser.Ecosystem;

    private static readonly HashSet<string> DevelopmentGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "dev", "develop", "development", "test", "tests", "testing", "lint", "linting", "docs", "doc", "typing", "types", "format", "style",
    };

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new List<DependencyInfo>();
        foreach (var entry in TomlLite.Parse(content))
        {
            var table = entry.TableName;
            var key = string.Join('.', entry.Key);

            if (table == "project" && key == "dependencies")
            {
                AddRequirements(result, entry.Value, isDevelopment: false, manifestPath);
            }
            else if (table == "project.optional-dependencies")
            {
                AddRequirements(result, entry.Value, DevelopmentGroups.Contains(key), manifestPath);
            }
            else if (table == "dependency-groups")
            {
                AddRequirements(result, entry.Value, isDevelopment: true, manifestPath);
            }
            else if (table is "tool.poetry.dependencies" or "tool.poetry.dev-dependencies"
                     || (table.StartsWith("tool.poetry.group.", StringComparison.Ordinal) && table.EndsWith(".dependencies", StringComparison.Ordinal)))
            {
                if (entry.Key.Count != 1 || key.Equals("python", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isDevelopment = table != "tool.poetry.dependencies" && table != "tool.poetry.group.main.dependencies";
                result.Add(new DependencyInfo(Ecosystem, key, PoetryVersion(entry.Value), isDevelopment, manifestPath));
            }
        }

        return result;
    }

    private static void AddRequirements(List<DependencyInfo> result, string value, bool isDevelopment, string manifestPath)
    {
        foreach (var requirement in TomlLite.GetStringArray(value))
        {
            if (PythonRequirement.TryParse(requirement, out var name, out var version))
            {
                result.Add(new DependencyInfo(Ecosystem, name, version, isDevelopment, manifestPath));
            }
        }
    }

    private static string? PoetryVersion(string value)
    {
        if (TomlLite.TryGetString(value, out var version))
        {
            return version.Length == 0 || version == "*" ? null : version;
        }

        return TomlLite.GetInlineTable(value).TryGetValue("version", out var raw) && TomlLite.TryGetString(raw, out var inline) ? inline : null;
    }
}
