using System.Xml;
using System.Xml.Linq;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>.NET: solutions and C#/F#/VB projects — SDK flavors, UI stacks, test frameworks, dotnet CLI commands.</summary>
internal sealed class DotNetDetector : IEcosystemDetector
{
    public const int MaxProjects = 60;
    public const int MaxRunnableProjects = 4;
    private const int MaxProjectFileBytes = 1024 * 1024;

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public string Name => ".NET";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var projectPaths = context.Files
            .Where(f => ProjectExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(RelativePaths.Depth)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        var solutions = context.Files
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(RelativePaths.Depth)
            .ThenBy(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) // .sln builds with every SDK version
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (projectPaths.Count == 0 && solutions.Count == 0)
        {
            return;
        }

        context.AddBuildSystem(".NET SDK");
        context.AddTechnology("NuGet", TechnologyKind.PackageManager, projectPaths.FirstOrDefault() ?? solutions[0]);

        var projects = new List<DotNetProject>();
        foreach (var path in projectPaths.Take(MaxProjects))
        {
            var text = await context.ReadTextAsync(path, cancellationToken, MaxProjectFileBytes).ConfigureAwait(false);
            if (text is null)
            {
                continue;
            }

            var project = DotNetProject.TryParse(path, text);
            if (project is null)
            {
                context.Notes.Add($"{path} is not valid XML; it was skipped.");
                continue;
            }

            projects.Add(project);
        }

        foreach (var project in projects)
        {
            AddTechnologies(context, project);
        }

        AddTestInfo(context, projects);
        AddCommands(context, solutions.FirstOrDefault(), projects);
    }

    private static void AddTechnologies(DetectionContext context, DotNetProject project)
    {
        var evidence = project.Path;
        context.AddTechnology(".NET", TechnologyKind.Runtime,
            project.TargetFrameworks.Count > 0 ? $"{string.Join(", ", project.TargetFrameworks)} in {evidence}" : evidence);
        context.AddTechnology(project.Language, TechnologyKind.Language, evidence);

        foreach (var sdk in project.Sdks)
        {
            switch (sdk)
            {
                case var s when s.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase):
                    context.AddTechnology("ASP.NET Core", TechnologyKind.Framework, evidence);
                    break;
                case var s when s.Equals("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase):
                    context.AddTechnology("Blazor", TechnologyKind.Framework, evidence);
                    break;
                case var s when s.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase):
                    context.AddTechnology("Worker Service", TechnologyKind.Framework, evidence);
                    break;
                case var s when s.StartsWith("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase):
                    context.AddTechnology(".NET Aspire", TechnologyKind.Framework, evidence);
                    break;
                case var s when s.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase):
                    context.AddTechnology("MSTest", TechnologyKind.TestFramework, evidence);
                    break;
            }
        }

        if (project.UseWpf)
        {
            context.AddTechnology("WPF", TechnologyKind.Framework, evidence);
        }

        if (project.UseWindowsForms)
        {
            context.AddTechnology("WinForms", TechnologyKind.Framework, evidence);
        }

        if (project.UseMaui)
        {
            context.AddTechnology("MAUI", TechnologyKind.Framework, evidence);
        }

        foreach (var package in project.Packages)
        {
            if (package.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) && !package.StartsWith("xunit.runner", StringComparison.OrdinalIgnoreCase)
                && !package.StartsWith("xunit.analyzers", StringComparison.OrdinalIgnoreCase))
            {
                context.AddTechnology("xUnit", TechnologyKind.TestFramework, $"{package} in {evidence}");
            }
            else if (package.Equals("NUnit", StringComparison.OrdinalIgnoreCase))
            {
                context.AddTechnology("NUnit", TechnologyKind.TestFramework, $"{package} in {evidence}");
            }
            else if (package.Equals("MSTest", StringComparison.OrdinalIgnoreCase) || package.Equals("MSTest.TestFramework", StringComparison.OrdinalIgnoreCase))
            {
                context.AddTechnology("MSTest", TechnologyKind.TestFramework, $"{package} in {evidence}");
            }
            else if (package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
            {
                context.AddTechnology("EF Core", TechnologyKind.Library, $"{package} in {evidence}");
            }
            else if (package.StartsWith("Microsoft.AspNetCore.Components.WebAssembly", StringComparison.OrdinalIgnoreCase))
            {
                context.AddTechnology("Blazor", TechnologyKind.Framework, $"{package} in {evidence}");
            }
            else if (package.Equals("Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase))
            {
                context.AddTechnology(".NET Aspire", TechnologyKind.Framework, $"{package} in {evidence}");
            }
        }
    }

    private static void AddTestInfo(DetectionContext context, List<DotNetProject> projects)
    {
        foreach (var project in projects.Where(p => p.IsTest))
        {
            context.TestLocations.Add(project.Directory.Length == 0 ? "." : project.Directory);
            foreach (var framework in project.TestFrameworks)
            {
                context.AddTestFramework(framework);
            }
        }
    }

    private static void AddCommands(DetectionContext context, string? solution, List<DotNetProject> projects)
    {
        var runnable = projects.Where(p => p.IsRunnable).ToList();
        var hasTests = projects.Any(p => p.IsTest);

        // A single explicit target avoids "MSB1011: more than one project or solution file" errors.
        var target = solution ?? (projects.Count == 1 ? projects[0].Path : null);
        if (target is null)
        {
            foreach (var project in projects.Take(5))
            {
                var argument = CommandLines.PathArgument(project.Path);
                context.AddCommand(new DetectedCommand
                {
                    Id = $"dotnet:{(project.IsTest ? "test" : "build")}:{project.Name}",
                    Name = $"{(project.IsTest ? "Test" : "Build")} {project.Name}",
                    CommandLine = $"dotnet {(project.IsTest ? "test" : "build")} {argument}",
                    Category = project.IsTest ? CommandCategory.Test : CommandCategory.Build,
                    Source = project.Path,
                });
            }
        }
        else
        {
            var argument = CommandLines.PathArgument(target);
            Add(context, "dotnet:restore", "Restore packages", $"dotnet restore {argument}", CommandCategory.Install, target);
            Add(context, "dotnet:build", "Build", $"dotnet build {argument}", CommandCategory.Build, target);
            if (hasTests)
            {
                Add(context, "dotnet:test", "Test", $"dotnet test {argument}", CommandCategory.Test, target);
            }

            Add(context, "dotnet:format", "Format", $"dotnet format {argument}", CommandCategory.Format, target);
            var publishTarget = runnable.Count == 1 ? runnable[0].Path : target;
            Add(context, "dotnet:publish", "Publish (Release)", $"dotnet publish {CommandLines.PathArgument(publishTarget)} -c Release", CommandCategory.Package, publishTarget);
            Add(context, "dotnet:clean", "Clean", $"dotnet clean {argument}", CommandCategory.Clean, target);
        }

        if (runnable.Count is > 0 and <= MaxRunnableProjects)
        {
            foreach (var project in runnable)
            {
                Add(context, $"dotnet:run:{project.Name}", $"Run {project.Name}", $"dotnet run --project {CommandLines.PathArgument(project.Path)}", CommandCategory.Run, project.Path);
            }
        }

        static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category, string source) =>
            context.AddCommand(new DetectedCommand { Id = id, Name = name, CommandLine = commandLine, Category = category, Source = source });
    }

    internal sealed record DotNetProject
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public required string Directory { get; init; }
        public required string Language { get; init; }
        public IReadOnlyList<string> Sdks { get; init; } = [];
        public IReadOnlyList<string> TargetFrameworks { get; init; } = [];
        public IReadOnlyList<string> Packages { get; init; } = [];
        public string? OutputType { get; init; }
        public bool UseWpf { get; init; }
        public bool UseWindowsForms { get; init; }
        public bool UseMaui { get; init; }
        public bool IsTestProjectProperty { get; init; }

        public IEnumerable<string> TestFrameworks
        {
            get
            {
                if (Packages.Any(p => p.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return "xUnit";
                }

                if (Packages.Any(p => p.Equals("NUnit", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return "NUnit";
                }

                if (Packages.Any(p => p.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)) || Sdks.Any(s => s.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return "MSTest";
                }
            }
        }

        public bool IsTest =>
            IsTestProjectProperty
            || TestFrameworks.Any()
            || Packages.Any(p => p.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));

        public bool IsRunnable
        {
            get
            {
                if (IsTest)
                {
                    return false;
                }

                if (OutputType is { } type)
                {
                    return type.Equals("Exe", StringComparison.OrdinalIgnoreCase) || type.Equals("WinExe", StringComparison.OrdinalIgnoreCase);
                }

                // These SDKs produce executables by default.
                return UseMaui || Sdks.Any(s =>
                    s.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase));
            }
        }

        public static DotNetProject? TryParse(string path, string text)
        {
            XDocument document;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(new StringReader(text), settings);
                document = XDocument.Load(reader);
            }
            catch (XmlException)
            {
                return null;
            }

            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            var sdks = new List<string>();
            if (root.Attribute("Sdk")?.Value is { } sdkAttribute)
            {
                sdks.AddRange(sdkAttribute.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            sdks.AddRange(root.Elements().Where(e => e.Name.LocalName == "Sdk").Select(e => e.Attribute("Name")?.Value).OfType<string>());

            string? Property(string name) => root.Descendants().LastOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            bool Flag(string name) => string.Equals(Property(name), "true", StringComparison.OrdinalIgnoreCase);

            var frameworks = (Property("TargetFrameworks") ?? Property("TargetFramework") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(f => !f.Contains('$', StringComparison.Ordinal))
                .ToList();

            var packages = root.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var extension = System.IO.Path.GetExtension(path);
            return new DotNetProject
            {
                Path = path,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Directory = RelativePaths.DirectoryOf(path),
                Language = extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ? "F#"
                    : extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ? "Visual Basic .NET"
                    : "C#",
                Sdks = sdks,
                TargetFrameworks = frameworks,
                Packages = packages,
                OutputType = Property("OutputType"),
                UseWpf = Flag("UseWPF"),
                UseWindowsForms = Flag("UseWindowsForms"),
                UseMaui = Flag("UseMaui"),
                IsTestProjectProperty = Flag("IsTestProject"),
            };
        }
    }
}
