using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Detection;

/// <summary>Combines the test frameworks ecosystems reported with the test folders and files found on disk.</summary>
internal static partial class TestDiscovery
{
    public const int MaxLocations = 10;

    private static readonly FrozenSet<string> TestFolderNames = new[]
    {
        "test", "tests", "spec", "specs", "__tests__", "e2e", "testing", "cypress", "playwright",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static TestInfo Build(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var locations = new List<string>(context.TestLocations.Distinct(StringComparer.OrdinalIgnoreCase));

        void AddIfNew(string location)
        {
            if (!locations.Any(existing => Overlaps(existing, location)))
            {
                locations.Add(location);
            }
        }

        foreach (var named in context.Files.Select(NamedTestFolder).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(RelativePaths.Depth).ThenBy(p => p, StringComparer.Ordinal))
        {
            AddIfNew(named);
        }

        foreach (var directory in context.Files.Where(f => TestFileName().IsMatch(RelativePaths.FileName(f))).Select(RelativePaths.DirectoryOf)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(RelativePaths.Depth).ThenBy(p => p, StringComparer.Ordinal))
        {
            AddIfNew(directory.Length == 0 ? "." : directory);
        }

        var ordered = locations
            .OrderBy(RelativePaths.Depth)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(MaxLocations)
            .ToList();

        var frameworks = context.TestFrameworks.ToList();
        return new TestInfo(ordered.Count > 0 || frameworks.Count > 0, frameworks, ordered);
    }

    /// <summary>The path of the first test-named folder containing the file ("src/app/__tests__"), or null.</summary>
    private static string? NamedTestFolder(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (TestFolderNames.Contains(segments[i]))
            {
                return string.Join('/', segments, 0, i + 1);
            }
        }

        return null;
    }

    private static bool Overlaps(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) || RelativePaths.IsUnder(a, b) || RelativePaths.IsUnder(b, a);

    [GeneratedRegex(@"(\.(test|spec)\.[cm]?[jt]sx?$)|(^test_.+\.py$)|(_test\.(py|go|rb|dart|exs)$)|(_spec\.rb$)|((Tests?|Spec|Specs)\.(cs|fs|vb|java|kt|php|swift|scala)$)", RegexOptions.CultureInvariant)]
    private static partial Regex TestFileName();
}
