using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Dart and Flutter: pubspec.yaml, pub commands, per-platform Flutter builds.</summary>
internal sealed class DartDetector : IEcosystemDetector
{
    private static readonly (string Folder, string Target, string Name)[] FlutterPlatforms =
    [
        ("windows", "windows", "Build Windows app"),
        ("web", "web", "Build web app"),
        ("android", "apk", "Build Android APK"),
    ];

    public string Name => "Dart";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        if (!context.Exists("pubspec.yaml"))
        {
            return;
        }

        var text = await context.ReadTextAsync("pubspec.yaml", cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var lines = YamlOutline.Read(text);
        var dependencies = YamlOutline.ChildKeys(lines, "dependencies");
        var devDependencies = YamlOutline.ChildKeys(lines, "dev_dependencies");
        var isFlutter = dependencies.Contains("flutter", StringComparer.Ordinal) || YamlOutline.FindTopLevel(lines, "flutter") >= 0;

        context.AddTechnology("Dart", TechnologyKind.Language, "pubspec.yaml");
        context.AddTechnology("pub", TechnologyKind.PackageManager, "pubspec.yaml");
        var hasTests = context.HasDirectory("test");
        if (hasTests)
        {
            context.TestLocations.Add("test");
        }

        if (isFlutter)
        {
            context.AddBuildSystem("Flutter");
            context.AddTechnology("Flutter", TechnologyKind.Framework, "pubspec.yaml");
            Add(context, "flutter:pub-get", "Get packages", "flutter pub get", CommandCategory.Install);
            Add(context, "flutter:run", "Run app", "flutter run", CommandCategory.Dev);
            Add(context, "flutter:analyze", "Analyze", "flutter analyze", CommandCategory.Lint);
            Add(context, "flutter:test", "Test", "flutter test", CommandCategory.Test);
            foreach (var platform in FlutterPlatforms.Where(p => context.HasDirectory(p.Folder)))
            {
                Add(context, $"flutter:build-{platform.Target}", platform.Name, $"flutter build {platform.Target}", CommandCategory.Package);
            }

            if (devDependencies.Contains("flutter_test", StringComparer.Ordinal) || hasTests)
            {
                context.AddTestFramework("flutter_test");
            }
        }
        else
        {
            context.AddBuildSystem("Dart");
            Add(context, "dart:pub-get", "Get packages", "dart pub get", CommandCategory.Install);
            Add(context, "dart:analyze", "Analyze", "dart analyze", CommandCategory.Lint);
            if (context.HasDirectory("bin"))
            {
                Add(context, "dart:run", "Run", "dart run", CommandCategory.Run);
            }

            if (devDependencies.Contains("test", StringComparer.Ordinal) || hasTests)
            {
                context.AddTestFramework("package:test");
                Add(context, "dart:test", "Test", "dart test", CommandCategory.Test);
            }
        }

        Add(context, "dart:format", "Format", "dart format .", CommandCategory.Format);
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category) =>
        context.AddCommand(new DetectedCommand { Id = id, Name = name, CommandLine = commandLine, Category = category, Source = "pubspec.yaml" });
}
