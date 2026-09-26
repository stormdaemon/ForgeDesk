using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Detection;

/// <summary>
/// Composite detector: builds a bounded <see cref="DetectionContext"/>, lets every registered
/// <see cref="IEcosystemDetector"/> contribute (a failing one only adds a note), then computes the
/// ecosystem-independent parts of the profile (languages, structure, workflows, tests…).
/// </summary>
internal sealed class ProjectDetector : IProjectDetector
{
    public const int DefaultMaxFiles = 50_000;
    public const int MaxWorkflows = 30;
    private const int MaxWorkflowBytes = 256 * 1024;

    // Display order of command categories: what developers reach for first comes first.
    private static readonly CommandCategory[] CategoryOrder =
    [
        CommandCategory.Dev, CommandCategory.Run, CommandCategory.Build, CommandCategory.Test, CommandCategory.Lint,
        CommandCategory.Format, CommandCategory.Install, CommandCategory.Package, CommandCategory.Deploy,
        CommandCategory.Clean, CommandCategory.Other,
    ];

    private readonly IReadOnlyList<IEcosystemDetector> _detectors;
    private readonly ProjectFileScanner _scanner;
    private readonly IClock _clock;
    private readonly ILogger<ProjectDetector> _logger;

    public ProjectDetector(IEnumerable<IEcosystemDetector> detectors, IGitService git, IClock clock, ILogger<ProjectDetector>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = detectors.ToList();
        _clock = clock;
        _logger = logger ?? NullLogger<ProjectDetector>.Instance;
        _scanner = new ProjectFileScanner(git, _logger);
    }

    /// <summary>Upper bound of files considered (the scan is flagged as truncated beyond it).</summary>
    internal int MaxFiles { get; init; } = DefaultMaxFiles;

    public async Task<ProjectProfile> DetectAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = PathUtil.Normalize(projectRoot);
        if (!Directory.Exists(root))
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{root}' does not exist.",
                "The project may have been moved or deleted. Locate it again or remove it from ForgeDesk.");
        }

        var scan = await _scanner.ScanAsync(root, MaxFiles, cancellationToken).ConfigureAwait(false);
        var context = new DetectionContext(root, scan.Files.Select(f => f.RelativePath).ToList(), scan.Truncated);

        foreach (var detector in _detectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await detector.ContributeAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ecosystem detector {Detector} failed for {Root}", detector.Name, root);
                context.Notes.Add($"{detector.Name} detection failed: {ex.Message}");
            }
        }

        var languages = LanguageStatistics.Compute(scan.Files);
        var workflows = await ReadWorkflowsAsync(context, cancellationToken).ConfigureAwait(false);
        var structure = await Task.Run(() => StructureAnalyzer.Describe(root, context), cancellationToken).ConfigureAwait(false);
        var notes = context.Notes.ToList();
        if (scan.Truncated)
        {
            notes.Insert(0, $"This project has more than {MaxFiles:N0} files; only the first {MaxFiles:N0} were analyzed.");
        }

        return new ProjectProfile
        {
            DetectedAt = _clock.Now,
            PrimaryLanguage = languages.FirstOrDefault(l => l.Language != LanguageStatistics.OtherLanguage)?.Language,
            Languages = languages,
            Technologies = context.Technologies.ToList(),
            BuildSystems = context.BuildSystems.ToList(),
            Commands = SortCommands(context.Commands),
            Tests = TestDiscovery.Build(context),
            Workflows = workflows,
            Structure = structure,
            ImportantFiles = ImportantFileFinder.Find(context),
            FileCount = scan.Files.Count,
            TotalBytes = scan.Files.Sum(f => f.Length),
            ScanTruncated = scan.Truncated,
            IsMonorepo = context.IsMonorepo,
            Notes = notes,
        };
    }

    internal static IReadOnlyList<DetectedCommand> SortCommands(IEnumerable<DetectedCommand> commands) =>
        commands
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(c => Array.IndexOf(CategoryOrder, c.Category) is var index and >= 0 ? index : CategoryOrder.Length)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

    private static async Task<IReadOnlyList<WorkflowFile>> ReadWorkflowsAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var workflows = new List<WorkflowFile>();
        foreach (var path in context.Files.Where(ImportantFileFinder.IsWorkflowFile).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(MaxWorkflows))
        {
            var text = await context.ReadTextAsync(path, cancellationToken, MaxWorkflowBytes).ConfigureAwait(false);
            workflows.Add(text is null
                ? new WorkflowFile(Path.GetFileNameWithoutExtension(path), path, [])
                : WorkflowFileParser.Parse(path, text));
        }

        return workflows;
    }
}
