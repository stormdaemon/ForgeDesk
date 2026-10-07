using ForgeDesk.Core.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Files;

/// <summary>
/// Quick open (Ctrl+P, the palette's "/" mode): fuzzy-searches the file index of the project on
/// screen and opens the chosen file in the Files tab. With an empty query in file mode, it
/// suggests the files git reports as changed. In the unprefixed palette, a few best file matches
/// join the other results.
/// </summary>
public sealed class FilesPaletteSource : IPaletteSource
{
    /// <summary>Results in the "/" file mode.</summary>
    public const int MaxFileModeResults = 50;

    /// <summary>Results mixed into the unprefixed palette.</summary>
    public const int MaxMixedResults = 8;

    private readonly INavigationService _navigation;
    private readonly Lazy<IFileIndex?> _index;
    private readonly ILogger<FilesPaletteSource> _logger;

    /// <summary>
    /// The file index is resolved on first use: the palette is built with the shell at startup and
    /// must not pull in the file services (nor fail where they are not registered).
    /// </summary>
    public FilesPaletteSource(INavigationService navigation, IServiceProvider services, ILogger<FilesPaletteSource> logger)
        : this(navigation, new Lazy<IFileIndex?>(() => services.GetService(typeof(IFileIndex)) as IFileIndex), logger)
    {
    }

    internal FilesPaletteSource(INavigationService navigation, IFileIndex index, ILogger<FilesPaletteSource> logger)
        : this(navigation, new Lazy<IFileIndex?>(index), logger)
    {
    }

    private FilesPaletteSource(INavigationService navigation, Lazy<IFileIndex?> index, ILogger<FilesPaletteSource> logger)
    {
        _navigation = navigation;
        _index = index;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.Wants(PaletteCategory.File)
            || _navigation.CurrentPage is not ProjectWorkspaceViewModel workspace
            || !workspace.AvailableTabs.Any(t => t.Section == WorkspaceSection.Files))
        {
            return [];
        }

        var fileMode = query.Categories is { Count: 1 } categories && categories.Contains(PaletteCategory.File);
        var term = query.Text.Trim();
        if (term.Length == 0)
        {
            return fileMode ? ChangedFiles(workspace) : [];
        }

        // In the mixed palette a single letter would flood the results with files.
        if (!fileMode && term.Length < 2)
        {
            return [];
        }

        try
        {
            if (_index.Value is not { } index)
            {
                return [];
            }

            var snapshot = await index.GetAsync(workspace.Context.Root, false, cancellationToken).ConfigureAwait(false);
            var max = fileMode ? MaxFileModeResults : MaxMixedResults;
            var matches = index.Search(snapshot, term, max);
            var items = new List<PaletteItem>(matches.Count);
            for (var i = 0; i < matches.Count; i++)
            {
                items.Add(Item(workspace, matches[i].RelativePath, RankBoost(i, matches.Count, fileMode)));
            }

            return items;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not search the files of {Root}", workspace.Context.Root);
            return [];
        }
    }

    /// <summary>
    /// Keeps the file index's ordering (it scores file names and consecutive matches) through the
    /// palette's own ranking: earlier results get a larger boost. Mixed results get none so actions
    /// and projects stay first.
    /// </summary>
    internal static double RankBoost(int index, int count, bool fileMode) =>
        fileMode ? 2.0 * (count - index) / Math.Max(1, count) : 0;

    private static List<PaletteItem> ChangedFiles(ProjectWorkspaceViewModel workspace)
    {
        var decorations = FileDecorations.From(workspace.Context.GitStatus);
        return decorations.Entries
            .Select(e => FileLocation.Normalize(e.Path)!)
            .Where(path => path.Length > 0 && decorations.StateOf(path) != FileGitState.Deleted)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxFileModeResults)
            .Select(path => Item(workspace, path, 0, FileDecorations.DescriptionOf(decorations.StateOf(path))))
            .ToList();
    }

    private static PaletteItem Item(ProjectWorkspaceViewModel workspace, string relativePath, double boost, string? state = null)
    {
        var path = relativePath.Replace('\\', '/');
        var folder = FileLocation.ParentOf(path);
        var subtitle = folder.Length == 0 ? workspace.Name : $"{workspace.Name}/{folder}";
        return new PaletteItem
        {
            Title = FileLocation.NameOf(path),
            Subtitle = state is null ? subtitle : $"{subtitle} · {state}",
            Icon = "Document20",
            Category = PaletteCategory.File,
            Keywords = path,
            Boost = boost,
            Execute = () =>
            {
                workspace.Context.RequestNavigation(WorkspaceSection.Files, new FileLocation(path));
                return Task.CompletedTask;
            },
        };
    }
}
