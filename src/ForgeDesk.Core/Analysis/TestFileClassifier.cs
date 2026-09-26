namespace ForgeDesk.Core.Analysis;

/// <summary>Recognizes test source files by the naming conventions of the major ecosystems.</summary>
internal static class TestFileClassifier
{
    private static readonly HashSet<string> TestFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "__tests__", "spec", "specs", "testing", "e2e", "__test__", "androidTest", "testFixtures",
    };

    private static readonly string[] TestProjectSuffixes = [".Tests", ".Test", ".UnitTests", ".IntegrationTests", ".Specs", ".FunctionalTests"];

    public static int Count(IEnumerable<ProjectFile> files) => files.Count(f => IsTestFile(f.RelativePath));

    public static bool IsTestFile(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (LanguageCatalog.Detect(relativePath) is null)
        {
            return false;
        }

        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var folder = segments[i];
            if (TestFolders.Contains(folder) || TestProjectSuffixes.Any(s => folder.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        var name = segments[^1];
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;

        return stem.EndsWith(".test", StringComparison.OrdinalIgnoreCase) // app.test.ts
            || stem.EndsWith(".spec", StringComparison.OrdinalIgnoreCase) // app.spec.js
            || stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase) // parser_test.go, utils_test.py
            || stem.EndsWith("_spec", StringComparison.OrdinalIgnoreCase) // user_spec.rb
            || stem.StartsWith("test_", StringComparison.OrdinalIgnoreCase) // test_utils.py
            || (stem.Length > 4 && (stem.EndsWith("Test", StringComparison.Ordinal) // ParserTest.java (capital T: not "Latest")
                || stem.EndsWith("Tests", StringComparison.Ordinal) // ParserTests.cs
                || stem.EndsWith("Spec", StringComparison.Ordinal) // ParserSpec.scala
                || stem.EndsWith("IT", StringComparison.Ordinal))); // ParserIT.java (integration)
    }
}
