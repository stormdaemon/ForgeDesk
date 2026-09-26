using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>Safe XML loading for manifests: no DTD processing, no external resolution.</summary>
internal static class ManifestXml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        MaxCharactersInDocument = 4 * 1024 * 1024,
    };

    public static XDocument? TryLoad(string content)
    {
        try
        {
            using var text = new StringReader(content);
            using var reader = XmlReader.Create(text, Settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Elements by local name, whatever their namespace (old csproj and pom files declare one).</summary>
    public static IEnumerable<XElement> Named(this XContainer container, string localName) =>
        container.Elements().Where(e => e.Name.LocalName == localName);

    public static string? ChildValue(this XElement element, string localName) =>
        element.Named(localName).FirstOrDefault()?.Value.Trim() is { Length: > 0 } value ? value : null;

    public static string? AttributeValue(this XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;
}

/// <summary>
/// MSBuild projects (*.csproj, *.fsproj, *.vbproj): <c>PackageReference</c> items, with versions
/// from the attribute, a child element, <c>VersionOverride</c>, or central package management
/// (<c>Directory.Packages.props</c>). Packages of test projects and <c>PrivateAssets="all"</c>
/// build-time packages (analyzers, SourceLink…) are development dependencies.
/// </summary>
internal static class MsBuildDependencyParser
{
    public const string Ecosystem = "NuGet";

    private static readonly string[] TestPackages = ["Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest", "MSTest.TestFramework", "TUnit"];

    /// <summary>Package versions of a Directory.Packages.props file.</summary>
    public static IReadOnlyDictionary<string, string> ReadCentralVersions(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = ManifestXml.TryLoad(content);
        if (document?.Root is null)
        {
            return versions;
        }

        foreach (var item in document.Root.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
        {
            var name = item.AttributeValue("Include") ?? item.AttributeValue("Update");
            var version = item.AttributeValue("Version") ?? item.ChildValue("Version");
            if (name is not null && version is not null)
            {
                versions[name] = version;
            }
        }

        return versions;
    }

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath, IReadOnlyDictionary<string, string>? centralVersions = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var document = ManifestXml.TryLoad(content);
        if (document?.Root is null)
        {
            return [];
        }

        var references = document.Root.Descendants().Where(e => e.Name.LocalName == "PackageReference").ToList();
        var isTestProject =
            document.Root.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            || references.Any(r => TestPackages.Contains(r.AttributeValue("Include"), StringComparer.OrdinalIgnoreCase));

        var result = new List<DependencyInfo>();
        foreach (var reference in references)
        {
            // "Update" items modify references declared elsewhere; they are not dependencies themselves.
            var name = reference.AttributeValue("Include");
            if (name is null)
            {
                continue;
            }

            var version = reference.AttributeValue("VersionOverride")
                ?? reference.AttributeValue("Version")
                ?? reference.ChildValue("VersionOverride")
                ?? reference.ChildValue("Version")
                ?? centralVersions?.GetValueOrDefault(name);
            var privateAssets = reference.AttributeValue("PrivateAssets") ?? reference.ChildValue("PrivateAssets");
            var isBuildTime = privateAssets is not null && privateAssets.Equals("all", StringComparison.OrdinalIgnoreCase);
            result.Add(new DependencyInfo(Ecosystem, name, version, isTestProject || isBuildTime, manifestPath));
        }

        return result;
    }
}

/// <summary>
/// Maven pom.xml: <c>project/dependencies/dependency</c> (not dependencyManagement or plugins),
/// named "groupId:artifactId", with <c>${property}</c> versions resolved from the pom's properties.
/// Test-scoped dependencies are development dependencies.
/// </summary>
internal static partial class MavenDependencyParser
{
    public const string Ecosystem = "Maven";

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        var root = ManifestXml.TryLoad(content)?.Root;
        if (root is null || root.Name.LocalName != "project")
        {
            return [];
        }

        var properties = root.Named("properties").Elements()
            .GroupBy(e => e.Name.LocalName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.Ordinal);
        properties.TryAdd("project.version", root.ChildValue("version") ?? string.Empty);

        var result = new List<DependencyInfo>();
        foreach (var dependency in root.Named("dependencies").SelectMany(d => d.Named("dependency")))
        {
            var groupId = dependency.ChildValue("groupId");
            var artifactId = dependency.ChildValue("artifactId");
            if (artifactId is null)
            {
                continue;
            }

            var version = dependency.ChildValue("version") is { } raw ? Resolve(raw, properties) : null;
            var scope = dependency.ChildValue("scope");
            result.Add(new DependencyInfo(Ecosystem, groupId is null ? artifactId : $"{groupId}:{artifactId}", version,
                string.Equals(scope, "test", StringComparison.OrdinalIgnoreCase), manifestPath));
        }

        return result;
    }

    private static string Resolve(string version, IReadOnlyDictionary<string, string> properties) =>
        Property().Replace(version, m => properties.TryGetValue(m.Groups["name"].Value, out var value) && value.Length > 0 ? value : m.Value);

    [GeneratedRegex(@"\$\{(?<name>[^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Property();
}
