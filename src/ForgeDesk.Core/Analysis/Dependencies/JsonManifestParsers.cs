using System.Text.Json;

namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>package.json: <c>dependencies</c> and <c>devDependencies</c>.</summary>
internal static class NpmDependencyParser
{
    public const string Ecosystem = "npm";

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath) =>
        JsonDependencyReader.Read(content, manifestPath, Ecosystem, ("dependencies", false), ("devDependencies", true));
}

/// <summary>composer.json: <c>require</c> and <c>require-dev</c>, without PHP itself and platform extensions.</summary>
internal static class ComposerDependencyParser
{
    public const string Ecosystem = "Composer";

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath) =>
        JsonDependencyReader.Read(content, manifestPath, Ecosystem, ("require", false), ("require-dev", true))
            .Where(d => !IsPlatformPackage(d.Name))
            .ToList();

    // "php", "ext-json", "lib-curl", "composer-plugin-api"… describe the platform, not packages.
    private static bool IsPlatformPackage(string name) =>
        name.Equals("php", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ext-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("lib-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("composer-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("php-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads "name": "version" objects of a JSON manifest, tolerating comments and trailing commas.</summary>
internal static class JsonDependencyReader
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    public static IReadOnlyList<DependencyInfo> Read(string content, string manifestPath, string ecosystem, params (string Property, bool IsDevelopment)[] sections)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            using var document = JsonDocument.Parse(content, Options);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var result = new List<DependencyInfo>();
            foreach (var (property, isDevelopment) in sections)
            {
                if (!document.RootElement.TryGetProperty(property, out var section) || section.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var dependency in section.EnumerateObject())
                {
                    var version = dependency.Value.ValueKind == JsonValueKind.String ? dependency.Value.GetString() : null;
                    result.Add(new DependencyInfo(ecosystem, dependency.Name, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), isDevelopment, manifestPath));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
