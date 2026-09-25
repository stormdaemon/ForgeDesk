namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Java/Kotlin with Gradle: build scripts, the Gradle wrapper, application/Spring Boot/Android plugins.</summary>
internal sealed class GradleDetector : IEcosystemDetector
{
    private static readonly string[] BuildScripts = ["build.gradle.kts", "build.gradle", "settings.gradle.kts", "settings.gradle"];

    public string Name => "Gradle";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var rootScript = context.FirstExisting(BuildScripts);
        if (rootScript is null)
        {
            return;
        }

        // Plugins are often applied in module scripts (app/build.gradle): read the root and first-level modules.
        var scripts = context.Files
            .Where(f => RelativePaths.Depth(f) <= 1 && BuildScripts.Contains(RelativePaths.FileName(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(RelativePaths.Depth)
            .ThenBy(f => f, StringComparer.Ordinal)
            .Take(12)
            .ToList();
        var text = new System.Text.StringBuilder();
        foreach (var script in scripts)
        {
            text.AppendLine(await context.ReadTextAsync(script, cancellationToken).ConfigureAwait(false));
        }

        var content = text.ToString();
        context.AddBuildSystem("Gradle");
        context.AddTechnology("Gradle", TechnologyKind.BuildTool, rootScript);
        var usesKotlin = rootScript.EndsWith(".kts", StringComparison.OrdinalIgnoreCase)
            || content.Contains("org.jetbrains.kotlin", StringComparison.Ordinal)
            || content.Contains("kotlin(\"", StringComparison.Ordinal);
        JvmLanguages.Add(context, rootScript, usesKotlin);

        var isSpringBoot = content.Contains("org.springframework.boot", StringComparison.Ordinal);
        var isAndroid = content.Contains("com.android.application", StringComparison.Ordinal) || content.Contains("com.android.library", StringComparison.Ordinal);
        var isApplication = content.Contains("application", StringComparison.Ordinal) && content.Contains("mainClass", StringComparison.Ordinal);

        if (isSpringBoot)
        {
            context.AddTechnology("Spring Boot", TechnologyKind.Framework, rootScript);
        }

        if (isAndroid)
        {
            context.AddTechnology("Android", TechnologyKind.Framework, rootScript);
        }

        JvmLanguages.AddTests(context, content);

        var gradle = GradleExecutable(context);
        Add(context, "build", "Build", $"{gradle} build", CommandCategory.Build, rootScript);
        Add(context, "test", "Test", $"{gradle} test", CommandCategory.Test, rootScript);
        Add(context, "clean", "Clean", $"{gradle} clean", CommandCategory.Clean, rootScript);
        if (isSpringBoot)
        {
            Add(context, "bootRun", "Run Spring Boot app", $"{gradle} bootRun", CommandCategory.Run, rootScript);
        }
        else if (isApplication)
        {
            Add(context, "run", "Run", $"{gradle} run", CommandCategory.Run, rootScript);
        }

        if (isAndroid)
        {
            Add(context, "assembleDebug", "Assemble debug APK", $"{gradle} assembleDebug", CommandCategory.Package, rootScript);
        }
    }

    /// <summary>The Gradle wrapper pins the Gradle version: prefer it, with the Windows script on Windows.</summary>
    private static string GradleExecutable(DetectionContext context)
    {
        if (OperatingSystem.IsWindows() && context.FirstExisting("gradlew.bat") is { } windowsWrapper)
        {
            return CommandLines.ProjectScript(windowsWrapper);
        }

        if (!OperatingSystem.IsWindows() && context.FirstExisting("gradlew") is { } unixWrapper)
        {
            return CommandLines.ProjectScript(unixWrapper);
        }

        return "gradle";
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category, string source) =>
        context.AddCommand(new DetectedCommand { Id = $"gradle:{id}", Name = name, CommandLine = commandLine, Category = category, Source = source });
}
