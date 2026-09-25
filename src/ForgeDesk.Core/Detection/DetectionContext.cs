namespace ForgeDesk.Core.Detection;

/// <summary>
/// Shared, bounded view of a project folder handed to every <see cref="IEcosystemDetector"/>,
/// plus the collectors they write their findings into.
/// </summary>
public sealed class DetectionContext
{
    public DetectionContext(string root, IReadOnlyList<string> relativeFiles, bool truncated)
    {
        Root = root;
        Files = relativeFiles;
        Truncated = truncated;
        FileSet = new HashSet<string>(relativeFiles, StringComparer.OrdinalIgnoreCase);
    }

    public string Root { get; }

    /// <summary>Relative paths (forward slashes) of the scanned files, heavy folders excluded.</summary>
    public IReadOnlyList<string> Files { get; }

    public HashSet<string> FileSet { get; }

    public bool Truncated { get; }

    public List<Technology> Technologies { get; } = [];
    public List<string> BuildSystems { get; } = [];
    public List<DetectedCommand> Commands { get; } = [];
    public List<string> TestFrameworks { get; } = [];
    public List<string> TestLocations { get; } = [];
    public List<string> Notes { get; } = [];
    public bool IsMonorepo { get; set; }

    public bool Exists(string relativePath) => FileSet.Contains(relativePath);

    public string FullPath(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public IEnumerable<string> FilesNamed(string fileName) =>
        Files.Where(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> FilesWithExtension(string extension) =>
        Files.Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads a small text file safely; returns null when missing, unreadable or larger than the limit.</summary>
    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken, int maxBytes = 2 * 1024 * 1024)
    {
        var full = FullPath(relativePath);
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > maxBytes)
            {
                return null;
            }

            return await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void AddTechnology(string name, TechnologyKind kind, string evidence)
    {
        if (!Technologies.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Technologies.Add(new Technology(name, kind, evidence));
        }
    }

    public void AddBuildSystem(string name)
    {
        if (!BuildSystems.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            BuildSystems.Add(name);
        }
    }

    public void AddCommand(DetectedCommand command)
    {
        if (!Commands.Any(c => c.Id == command.Id))
        {
            Commands.Add(command);
        }
    }

    public void AddTestFramework(string name)
    {
        if (!TestFrameworks.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            TestFrameworks.Add(name);
        }
    }
}
