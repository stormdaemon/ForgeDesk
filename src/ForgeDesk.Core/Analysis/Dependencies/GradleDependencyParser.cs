using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>
/// build.gradle / build.gradle.kts: dependency declarations such as
/// <c>implementation("group:name:1.0")</c>, <c>testImplementation 'g:n:1'</c>,
/// <c>api(group = "g", name = "n", version = "1")</c> and version-catalog accessors
/// (<c>implementation(libs.androidx.core)</c>) resolved through gradle/libs.versions.toml.
/// Project dependencies, platforms and file dependencies are not external libraries and are skipped.
/// </summary>
internal static partial class GradleDependencyParser
{
    public const string Ecosystem = "Gradle";

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath, GradleVersionCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new List<DependencyInfo>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var declaration = Declaration().Match(line);
            if (!declaration.Success)
            {
                continue;
            }

            var configuration = declaration.Groups["configuration"].Value;
            var isDevelopment = configuration.StartsWith("test", StringComparison.Ordinal)
                || configuration.StartsWith("androidTest", StringComparison.Ordinal)
                || configuration.StartsWith("debug", StringComparison.Ordinal)
                || configuration == "classpath";
            var arguments = declaration.Groups["arguments"].Value;

            if (Coordinates().Match(arguments) is { Success: true } coordinates)
            {
                var version = coordinates.Groups["version"].Success ? coordinates.Groups["version"].Value : null;
                result.Add(new DependencyInfo(Ecosystem, $"{coordinates.Groups["group"].Value}:{coordinates.Groups["name"].Value}", version, isDevelopment, manifestPath));
            }
            else if (MapNotation().Match(arguments) is { Success: true } map)
            {
                var version = map.Groups["version"].Success ? map.Groups["version"].Value : null;
                result.Add(new DependencyInfo(Ecosystem, $"{map.Groups["group"].Value}:{map.Groups["name"].Value}", version, isDevelopment, manifestPath));
            }
            else if (catalog is not null && CatalogAccessor().Match(arguments) is { Success: true } accessor)
            {
                foreach (var library in catalog.Resolve(accessor.Groups["path"].Value))
                {
                    result.Add(new DependencyInfo(Ecosystem, library.Module, library.Version, isDevelopment, manifestPath));
                }
            }
        }

        return result;
    }

    [GeneratedRegex(
        @"^(?<configuration>implementation|api|compileOnly|runtimeOnly|compile|runtime|testImplementation|testCompileOnly|testRuntimeOnly|testCompile|androidTestImplementation|debugImplementation|kapt|ksp|annotationProcessor|classpath|developmentOnly)\b\s*\(?\s*(?<arguments>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();

    // "group:name:version" or "group:name:version@aar" / ":classifier" (the platform("…") wrapper is not matched on purpose).
    [GeneratedRegex(@"^[""'](?<group>[^""':\s$]+):(?<name>[^""':\s]+)(?::(?<version>[^""'@:\s]+))?[^""']*[""']", RegexOptions.CultureInvariant)]
    private static partial Regex Coordinates();

    [GeneratedRegex(@"group\s*[:=]\s*[""'](?<group>[^""']+)[""']\s*,\s*name\s*[:=]\s*[""'](?<name>[^""']+)[""'](?:\s*,\s*version\s*[:=]\s*[""'](?<version>[^""']+)[""'])?", RegexOptions.CultureInvariant)]
    private static partial Regex MapNotation();

    [GeneratedRegex(@"^libs\.(?<path>[A-Za-z0-9_.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogAccessor();
}

/// <summary>A Gradle version catalog (gradle/libs.versions.toml): libraries and bundles by accessor path.</summary>
internal sealed class GradleVersionCatalog
{
    private readonly Dictionary<string, (string Module, string? Version)> _libraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _bundles = new(StringComparer.OrdinalIgnoreCase);

    public static GradleVersionCatalog Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var catalog = new GradleVersionCatalog();
        var entries = TomlLite.Parse(content);
        var versions = entries
            .Where(e => e.TableName == "versions")
            .Select(e => (Key: string.Join('.', e.Key), Version: ReadVersion(e.Value)))
            .Where(v => v.Version is not null)
            .GroupBy(v => v.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Version!, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var alias = Accessor(string.Join('.', entry.Key));
            if (entry.TableName == "libraries")
            {
                if (ReadLibrary(entry.Value, versions) is { } library)
                {
                    catalog._libraries[alias] = library;
                }
            }
            else if (entry.TableName == "bundles")
            {
                catalog._bundles[alias] = TomlLite.GetStringArray(entry.Value).Select(Accessor).ToList();
            }
        }

        return catalog;
    }

    /// <summary>Libraries behind "androidx.core.ktx" or "bundles.compose"; empty when unknown (plugins, versions).</summary>
    public IReadOnlyList<(string Module, string? Version)> Resolve(string accessorPath)
    {
        var path = Accessor(accessorPath);
        if (path.StartsWith("bundles.", StringComparison.OrdinalIgnoreCase) && _bundles.TryGetValue(path["bundles.".Length..], out var bundle))
        {
            return bundle.Where(_libraries.ContainsKey).Select(a => _libraries[a]).ToList();
        }

        return _libraries.TryGetValue(path, out var library) ? [library] : [];
    }

    /// <summary>Gradle maps '-', '_' and '.' in aliases to the same accessor separator.</summary>
    private static string Accessor(string alias) => alias.Replace('-', '.').Replace('_', '.');

    private static (string Module, string? Version)? ReadLibrary(string value, IReadOnlyDictionary<string, string> versions)
    {
        if (TomlLite.TryGetString(value, out var notation))
        {
            var parts = notation.Split(':');
            return parts.Length >= 2 ? ($"{parts[0]}:{parts[1]}", parts.Length > 2 ? parts[2] : null) : null;
        }

        var table = TomlLite.GetInlineTable(value);
        string? module = null;
        if (table.TryGetValue("module", out var rawModule) && TomlLite.TryGetString(rawModule, out var m))
        {
            module = m;
        }
        else if (table.TryGetValue("group", out var rawGroup) && TomlLite.TryGetString(rawGroup, out var g)
                 && table.TryGetValue("name", out var rawName) && TomlLite.TryGetString(rawName, out var n))
        {
            module = $"{g}:{n}";
        }

        if (module is null)
        {
            return null;
        }

        string? version = null;
        if (table.TryGetValue("version.ref", out var reference) && TomlLite.TryGetString(reference, out var key))
        {
            version = versions.GetValueOrDefault(key);
        }
        else if (table.TryGetValue("version", out var rawVersion))
        {
            version = ReadVersion(rawVersion) ?? (TomlLite.GetInlineTable(rawVersion).TryGetValue("ref", out var nested) && TomlLite.TryGetString(nested, out var nestedKey)
                ? versions.GetValueOrDefault(nestedKey)
                : null);
        }

        return (module, version);
    }

    private static string? ReadVersion(string value)
    {
        if (TomlLite.TryGetString(value, out var version))
        {
            return version;
        }

        var table = TomlLite.GetInlineTable(value);
        foreach (var key in (string[])["strictly", "require", "prefer"])
        {
            if (table.TryGetValue(key, out var raw) && TomlLite.TryGetString(raw, out var constrained))
            {
                return constrained;
            }
        }

        return null;
    }
}
