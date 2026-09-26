using System.Text.RegularExpressions;
using ForgeDesk.Core.Detection.Parsing;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>
/// Python: pyproject.toml (Poetry, uv, Hatch, PDM, setuptools), requirements files, setup.py, Pipfile;
/// Django, Flask and FastAPI entry points; pytest, ruff, black, mypy and flake8 commands.
/// </summary>
internal sealed partial class PythonDetector : IEcosystemDetector
{
    private const int MaxSourceBytes = 256 * 1024;

    private static readonly string[] FastApiCandidates = ["main.py", "app.py", "app/main.py", "src/main.py", "api/main.py", "server.py"];

    public string Name => "Python";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var requirementFiles = context.Files
            .Where(f => (RelativePaths.IsAtRoot(f) || RelativePaths.DirectoryOf(f).Equals("requirements", StringComparison.OrdinalIgnoreCase))
                        && RelativePaths.FileName(f).StartsWith("requirements", StringComparison.OrdinalIgnoreCase)
                        && f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Length)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        var hasPyproject = context.Exists("pyproject.toml");
        var hasSetupPy = context.Exists("setup.py");
        var hasPipfile = context.Exists("Pipfile");
        // Django projects often live one folder down ("backend/manage.py").
        var managePy = context.FilesNamed("manage.py")
            .Where(f => RelativePaths.Depth(f) <= 1)
            .OrderBy(RelativePaths.Depth)
            .ThenBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!hasPyproject && !hasSetupPy && !hasPipfile && requirementFiles.Count == 0 && managePy is null)
        {
            return;
        }

        var evidence = hasPyproject ? "pyproject.toml" : hasPipfile ? "Pipfile" : hasSetupPy ? "setup.py" : requirementFiles.FirstOrDefault() ?? managePy!;
        context.AddTechnology("Python", TechnologyKind.Language, evidence);

        var project = new PythonProject();
        if (hasPyproject && await context.ReadTextAsync("pyproject.toml", cancellationToken).ConfigureAwait(false) is { } pyproject)
        {
            ReadPyproject(TomlLite.Parse(pyproject), project);
        }

        if (hasPipfile && await context.ReadTextAsync("Pipfile", cancellationToken).ConfigureAwait(false) is { } pipfile)
        {
            var toml = TomlLite.Parse(pipfile);
            project.AddDependencies(toml.KeysOf("packages"));
            project.AddDependencies(toml.KeysOf("dev-packages"));
            project.Tool ??= "Pipenv";
        }

        foreach (var file in requirementFiles)
        {
            if (await context.ReadTextAsync(file, cancellationToken).ConfigureAwait(false) is { } requirements)
            {
                project.AddDependencies(RequirementNames(requirements));
            }
        }

        if (hasSetupPy && await context.ReadTextAsync("setup.py", cancellationToken).ConfigureAwait(false) is { } setupPy)
        {
            project.AddDependencies(QuotedRequirement().Matches(setupPy).Select(m => m.Groups["name"].Value));
            project.Tool ??= "setuptools";
        }

        foreach (var (file, configuredTool) in new[] { ("pytest.ini", "pytest"), ("conftest.py", "pytest"), (".flake8", "flake8"), ("mypy.ini", "mypy"), ("ruff.toml", "ruff"), (".ruff.toml", "ruff") })
        {
            if (context.Exists(file) || (file == "conftest.py" && context.FilesNamed(file).Any()))
            {
                project.Tools.Add(configuredTool);
            }
        }

        if (context.Exists("setup.cfg") && await context.ReadTextAsync("setup.cfg", cancellationToken).ConfigureAwait(false) is { } setupCfg)
        {
            ReadIniSections(setupCfg, project);
        }

        if (context.Exists("tox.ini") && await context.ReadTextAsync("tox.ini", cancellationToken).ConfigureAwait(false) is { } toxIni)
        {
            ReadIniSections(toxIni, project);
        }

        var tool = project.Tool ?? (requirementFiles.Count > 0 || hasPyproject ? "pip" : null);
        if (context.Exists("uv.lock"))
        {
            tool = "uv";
        }
        else if (context.Exists("poetry.lock"))
        {
            tool = "Poetry";
        }
        else if (context.Exists("pdm.lock"))
        {
            tool = "PDM";
        }

        if (tool is not null)
        {
            context.AddBuildSystem(tool);
            context.AddTechnology(tool, TechnologyKind.PackageManager, evidence);
        }

        var runner = RunnerPrefix(tool);
        AddInstallCommand(context, tool, requirementFiles, hasPyproject || hasSetupPy);
        AddToolCommands(context, project, runner);
        await AddFrameworkCommandsAsync(context, project, runner, managePy, cancellationToken).ConfigureAwait(false);
        AddTests(context, project, runner);
    }

    private static void ReadPyproject(TomlLite toml, PythonProject project)
    {
        project.AddDependencies(toml.GetStringArray("project", "dependencies").Select(RequirementName));
        foreach (var table in new[] { "project.optional-dependencies", "dependency-groups" })
        {
            foreach (var entry in toml.Entries(table))
            {
                project.AddDependencies(TomlLite.StringsIn(entry.RawValue).Select(RequirementName));
            }
        }

        foreach (var table in toml.Tables.Where(t => t.StartsWith("tool.poetry", StringComparison.OrdinalIgnoreCase) && t.EndsWith("dependencies", StringComparison.OrdinalIgnoreCase)))
        {
            project.AddDependencies(toml.KeysOf(table));
        }

        foreach (var toolName in new[] { "pytest", "ruff", "black", "mypy", "flake8" })
        {
            if (toml.HasTableOrChild($"tool.{toolName}"))
            {
                project.Tools.Add(toolName);
            }
        }

        var backend = toml.GetString("build-system", "build-backend") ?? string.Empty;
        project.Tool =
            toml.HasTableOrChild("tool.poetry") || backend.StartsWith("poetry", StringComparison.OrdinalIgnoreCase) ? "Poetry"
            : toml.HasTableOrChild("tool.uv") ? "uv"
            : toml.HasTableOrChild("tool.pdm") || backend.StartsWith("pdm", StringComparison.OrdinalIgnoreCase) ? "PDM"
            : toml.HasTableOrChild("tool.hatch") || backend.StartsWith("hatchling", StringComparison.OrdinalIgnoreCase) ? "Hatch"
            : backend.StartsWith("setuptools", StringComparison.OrdinalIgnoreCase) ? "setuptools"
            : project.Tool;
    }

    /// <summary>Recognizes tool sections in setup.cfg / tox.ini ("[tool:pytest]", "[flake8]", "[mypy]").</summary>
    private static void ReadIniSections(string text, PythonProject project)
    {
        foreach (Match match in IniSection().Matches(text))
        {
            var section = match.Groups["name"].Value.ToLowerInvariant();
            if (section is "tool:pytest" or "pytest")
            {
                project.Tools.Add("pytest");
            }
            else if (section is "flake8" or "mypy")
            {
                project.Tools.Add(section);
            }
        }
    }

    private static string RunnerPrefix(string? tool) => tool switch
    {
        "Poetry" => "poetry run ",
        "uv" => "uv run ",
        "PDM" => "pdm run ",
        "Hatch" => "hatch run ",
        "Pipenv" => "pipenv run ",
        _ => string.Empty,
    };

    /// <summary>A tool invocation: through the project runner when there is one, else "python -m" (Scripts may not be on PATH on Windows).</summary>
    private static string Tool(string runner, string module, string arguments = "") =>
        (runner.Length > 0 ? $"{runner}{module}" : $"python -m {module}") + (arguments.Length > 0 ? " " + arguments : string.Empty);

    private static void AddInstallCommand(DetectionContext context, string? tool, List<string> requirementFiles, bool isInstallablePackage)
    {
        var commandLine = tool switch
        {
            "Poetry" => "poetry install",
            "uv" => "uv sync",
            "PDM" => "pdm install",
            "Pipenv" => "pipenv install --dev",
            "Hatch" => "hatch env create",
            _ when requirementFiles.Count > 0 => $"python -m pip install -r {CommandLines.PathArgument(requirementFiles[0])}",
            _ when isInstallablePackage => "python -m pip install -e .",
            _ => null,
        };

        if (commandLine is not null)
        {
            Add(context, "install", "Install dependencies", commandLine, CommandCategory.Install, requirementFiles.FirstOrDefault() ?? "pyproject.toml");
        }
    }

    private static void AddToolCommands(DetectionContext context, PythonProject project, string runner)
    {
        if (project.Uses("ruff"))
        {
            context.AddTechnology("Ruff", TechnologyKind.Tooling, "ruff");
            Add(context, "ruff", "Ruff check", Tool(runner, "ruff", "check ."), CommandCategory.Lint, "pyproject.toml");
            Add(context, "ruff-format", "Ruff format", Tool(runner, "ruff", "format ."), CommandCategory.Format, "pyproject.toml");
        }

        if (project.Uses("black"))
        {
            context.AddTechnology("Black", TechnologyKind.Tooling, "black");
            Add(context, "black", "Black", Tool(runner, "black", "."), CommandCategory.Format, "pyproject.toml");
        }

        if (project.Uses("mypy"))
        {
            context.AddTechnology("mypy", TechnologyKind.Tooling, "mypy");
            Add(context, "mypy", "mypy", Tool(runner, "mypy", "."), CommandCategory.Lint, "pyproject.toml");
        }

        if (project.Uses("flake8"))
        {
            context.AddTechnology("Flake8", TechnologyKind.Tooling, "flake8");
            Add(context, "flake8", "Flake8", Tool(runner, "flake8"), CommandCategory.Lint, "pyproject.toml");
        }
    }

    private static async Task AddFrameworkCommandsAsync(DetectionContext context, PythonProject project, string runner, string? manage, CancellationToken cancellationToken)
    {
        var python = runner.Length > 0 ? $"{runner}python" : "python";
        if (manage is not null || project.Uses("django"))
        {
            context.AddTechnology("Django", TechnologyKind.Framework, manage ?? "django dependency");
            if (manage is not null)
            {
                var directory = RelativePaths.DirectoryOf(manage);
                Add(context, "django-runserver", "Django server", $"{python} manage.py runserver", CommandCategory.Dev, manage, directory);
                Add(context, "django-test", "Django tests", $"{python} manage.py test", CommandCategory.Test, manage, directory);
                Add(context, "django-migrate", "Apply migrations", $"{python} manage.py migrate", CommandCategory.Run, manage, directory);
            }
        }

        if (project.Uses("flask"))
        {
            context.AddTechnology("Flask", TechnologyKind.Framework, "flask dependency");

            // "flask run" discovers app.py / wsgi.py on its own.
            if (context.Exists("app.py") || context.Exists("wsgi.py"))
            {
                Add(context, "flask-run", "Flask server", Tool(runner, "flask", "run --debug"), CommandCategory.Dev, context.Exists("app.py") ? "app.py" : "wsgi.py");
            }
        }

        if (project.Uses("fastapi"))
        {
            context.AddTechnology("FastAPI", TechnologyKind.Framework, "fastapi dependency");
            foreach (var candidate in FastApiCandidates.Where(context.Exists))
            {
                var text = await context.ReadTextAsync(candidate, cancellationToken, MaxSourceBytes).ConfigureAwait(false);
                if (text is null || FastApiInstance().Match(text) is not { Success: true } match)
                {
                    continue;
                }

                var module = candidate[..^3].Replace('/', '.');
                Add(context, "uvicorn", "FastAPI server", Tool(runner, "uvicorn", $"{module}:{match.Groups["variable"].Value} --reload"), CommandCategory.Dev, candidate);
                break;
            }
        }
    }

    private static void AddTests(DetectionContext context, PythonProject project, string runner)
    {
        var hasTestFiles = context.Files.Any(f => f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
            && (RelativePaths.FileName(f).StartsWith("test_", StringComparison.OrdinalIgnoreCase) || f.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)));

        if (project.Uses("pytest"))
        {
            context.AddTechnology("pytest", TechnologyKind.TestFramework, "pytest");
            context.AddTestFramework("pytest");
            Add(context, "pytest", "pytest", Tool(runner, "pytest"), CommandCategory.Test, "pyproject.toml");
        }
        else if (hasTestFiles)
        {
            context.AddTestFramework("unittest");
            Add(context, "unittest", "unittest", runner.Length > 0 ? $"{runner}python -m unittest discover" : "python -m unittest discover", CommandCategory.Test, "tests");
        }
    }

    internal static IEnumerable<string> RequirementNames(string requirements)
    {
        foreach (var rawLine in requirements.Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0 || line.StartsWith('-') || line.Contains("://", StringComparison.Ordinal))
            {
                continue;
            }

            var name = RequirementName(line);
            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    /// <summary>"Django>=4.2; python_version > '3.8'" → "django" (PEP 503 normalization).</summary>
    internal static string RequirementName(string requirement)
    {
        var match = RequirementNamePattern().Match(requirement.Trim());
        return match.Success ? match.Value.ToLowerInvariant().Replace('_', '-').Replace('.', '-') : string.Empty;
    }

    private static void Add(DetectionContext context, string id, string name, string commandLine, CommandCategory category, string source, string workingDirectory = "") =>
        context.AddCommand(new DetectedCommand
        {
            Id = $"python:{id}",
            Name = name,
            CommandLine = commandLine,
            Category = category,
            Source = source,
            WorkingDirectory = workingDirectory,
        });

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*", RegexOptions.CultureInvariant)]
    private static partial Regex RequirementNamePattern();

    [GeneratedRegex(@"['""](?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*(?:\[[^\]]*\])?\s*(?:[<>=!~;][^'""]*)?['""]", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedRequirement();

    [GeneratedRegex(@"^\[(?<name>[^\]]+)\]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex IniSection();

    [GeneratedRegex(@"^(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*[A-Za-z_.]+\s*)?=\s*(?:fastapi\.)?FastAPI\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FastApiInstance();

    private sealed class PythonProject
    {
        private readonly HashSet<string> _dependencies = new(StringComparer.OrdinalIgnoreCase);

        public string? Tool { get; set; }

        /// <summary>Tools configured through their own config files or sections.</summary>
        public HashSet<string> Tools { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddDependencies(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                var normalized = RequirementName(name);
                if (normalized.Length > 0)
                {
                    _dependencies.Add(normalized);
                }
            }
        }

        public bool Uses(string name) => _dependencies.Contains(name) || Tools.Contains(name);
    }
}
