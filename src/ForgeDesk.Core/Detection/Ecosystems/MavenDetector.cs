namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Java/Kotlin with Maven: pom.xml, the Maven wrapper, Spring Boot, JUnit.</summary>
internal sealed class MavenDetector : IEcosystemDetector
{
    public string Name => "Maven";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        if (!context.Exists("pom.xml"))
        {
            return;
        }

        var pom = await context.ReadTextAsync("pom.xml", cancellationToken).ConfigureAwait(false) ?? string.Empty;
        context.AddBuildSystem("Maven");
        context.AddTechnology("Maven", TechnologyKind.BuildTool, "pom.xml");
        JvmLanguages.Add(context, "pom.xml", pom.Contains("kotlin-maven-plugin", StringComparison.Ordinal));

        var isSpringBoot = pom.Contains("org.springframework.boot", StringComparison.Ordinal);
        if (isSpringBoot)
        {
            context.AddTechnology("Spring Boot", TechnologyKind.Framework, "pom.xml");
        }

        JvmLanguages.AddTests(context, pom);

        var maven = MavenExecutable(context);
        Add(context, "package", "Package", $"{maven} package", CommandCategory.Build);
        Add(context, "test", "Test", $"{maven} test", CommandCategory.Test);
        Add(context, "verify", "Verify", $"{maven} verify", CommandCategory.Test);
        Add(context, "clean", "Clean", $"{maven} clean", CommandCategory.Clean);
        if (isSpringBoot)
        {
            Add(context, "spring-boot-run", "Run Spring Boot app", $"{maven} spring-boot:run", CommandCategory.Run);
        }
    }

    /// <summary>The Maven wrapper pins the Maven version: prefer it, with the Windows script on Windows.</summary>
    private static string MavenExecutable(DetectionContext context)
    {
        if (OperatingSystem.IsWindows() && context.FirstExisting("mvnw.cmd") is { } windowsWrapper)
        {
            return CommandLines.ProjectScript(windowsWrapper);
        }

        if (!OperatingSystem.IsWindows() && context.FirstExisting("mvnw") is { } unixWrapper)
        {
            return CommandLines.ProjectScript(unixWrapper);
        }

        return "mvn";
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category) =>
        context.AddCommand(new DetectedCommand { Id = $"maven:{id}", Name = name, CommandLine = commandLine, Category = category, Source = "pom.xml" });
}
