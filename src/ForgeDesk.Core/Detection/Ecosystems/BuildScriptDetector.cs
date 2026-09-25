using System.Collections.Frozen;
using ForgeDesk.Core.Processes;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>
/// PowerShell and batch scripts at the project root named like tasks (build.ps1, test.cmd,
/// setup.bat…). Batch files only run on Windows, so they are only offered there.
/// </summary>
internal sealed class BuildScriptDetector : IEcosystemDetector
{
    private static readonly FrozenSet<string> TaskWords = new[]
    {
        "build", "test", "run", "start", "dev", "setup", "install", "bootstrap", "clean", "deploy", "release",
        "publish", "package", "pack", "format", "lint", "restore", "ci", "make",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly char[] NameSeparators = ['-', '_', '.', ' '];

    private readonly Func<string, bool> _isToolInstalled;

    public BuildScriptDetector()
        : this(static tool => ExecutableLocator.Find(tool) is not null)
    {
    }

    internal BuildScriptDetector(Func<string, bool> isToolInstalled)
    {
        _isToolInstalled = isToolInstalled;
    }

    public string Name => "Build scripts";

    public Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var scripts = context.FilesIn(string.Empty)
            .Where(f => IsTaskScript(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scripts.Count == 0)
        {
            return Task.CompletedTask;
        }

        // PowerShell 7 is preferred; Windows PowerShell 5.1 blocks local scripts unless the policy is bypassed.
        var powerShell = !OperatingSystem.IsWindows() || _isToolInstalled("pwsh")
            ? "pwsh -NoProfile -File"
            : "powershell -NoProfile -ExecutionPolicy Bypass -File";

        foreach (var script in scripts)
        {
            var extension = Path.GetExtension(script);
            var isPowerShell = extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
            if (!isPowerShell && !OperatingSystem.IsWindows())
            {
                continue;
            }

            if (isPowerShell)
            {
                context.AddTechnology("PowerShell", TechnologyKind.Tooling, script);
            }

            context.AddCommand(new DetectedCommand
            {
                Id = $"script:{script}",
                Name = script,
                CommandLine = isPowerShell ? $"{powerShell} {CommandLines.Quote(script)}" : CommandLines.ProjectScript(script),
                Category = CommandCategorizer.FromName(Path.GetFileNameWithoutExtension(script)),
                Source = script,
            });
        }

        return Task.CompletedTask;
    }

    private static bool IsTaskScript(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Wrappers of other build tools are handled by their own detectors.
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (baseName.Equals("gradlew", StringComparison.OrdinalIgnoreCase) || baseName.Equals("mvnw", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstWord = baseName.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstWord is not null && TaskWords.Contains(firstWord);
    }
}
