namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Language and test facts shared by the JVM build tools.</summary>
internal static class JvmLanguages
{
    public static void Add(DetectionContext context, string evidence, bool usesKotlin)
    {
        if (context.HasDirectory("src/main/java") || context.Files.Any(f => f.EndsWith(".java", StringComparison.OrdinalIgnoreCase)))
        {
            context.AddTechnology("Java", TechnologyKind.Language, evidence);
        }

        if (usesKotlin || context.Files.Any(f => f.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)))
        {
            context.AddTechnology("Kotlin", TechnologyKind.Language, evidence);
        }

        context.AddTechnology("JVM", TechnologyKind.Runtime, evidence);
    }

    public static void AddTests(DetectionContext context, string buildText)
    {
        if (buildText.Contains("junit", StringComparison.OrdinalIgnoreCase))
        {
            context.AddTestFramework("JUnit");
            context.AddTechnology("JUnit", TechnologyKind.TestFramework, "build script");
        }

        if (buildText.Contains("io.kotest", StringComparison.Ordinal))
        {
            context.AddTestFramework("Kotest");
        }

        foreach (var directory in new[] { "src/test", "src/androidTest", "app/src/test" })
        {
            if (context.HasDirectory(directory))
            {
                context.TestLocations.Add(directory);
            }
        }
    }
}
