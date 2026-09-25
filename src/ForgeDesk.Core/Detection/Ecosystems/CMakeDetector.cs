namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>CMake: configure / build / test (CTest) in an out-of-source "build" folder.</summary>
internal sealed class CMakeDetector : IEcosystemDetector
{
    public string Name => "CMake";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        if (!context.Exists("CMakeLists.txt"))
        {
            return;
        }

        context.AddBuildSystem("CMake");
        context.AddTechnology("CMake", TechnologyKind.BuildTool, "CMakeLists.txt");
        var text = await context.ReadTextAsync("CMakeLists.txt", cancellationToken).ConfigureAwait(false) ?? string.Empty;

        Add(context, "configure", "Configure", "cmake -S . -B build", CommandCategory.Build);
        Add(context, "build", "Build", "cmake --build build", CommandCategory.Build);

        var hasTests = text.Contains("enable_testing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("add_test", StringComparison.OrdinalIgnoreCase)
            || text.Contains("include(CTest)", StringComparison.OrdinalIgnoreCase);
        if (hasTests)
        {
            context.AddTestFramework("CTest");
            Add(context, "test", "Test", "ctest --test-dir build", CommandCategory.Test);
        }

        if (text.Contains("GTest", StringComparison.Ordinal) || text.Contains("gtest", StringComparison.Ordinal))
        {
            context.AddTestFramework("GoogleTest");
        }

        if (text.Contains("Catch2", StringComparison.Ordinal))
        {
            context.AddTestFramework("Catch2");
        }
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category) =>
        context.AddCommand(new DetectedCommand { Id = $"cmake:{id}", Name = name, CommandLine = commandLine, Category = category, Source = "CMakeLists.txt" });
}
