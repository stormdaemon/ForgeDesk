using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Palette;
using ForgeDesk.Presentation.Workspace;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Presentation.Git;

/// <summary>
/// Git entries of the command palette for the project on screen (when it is a repository): switch
/// to a local branch, commit, stash, and jump to the Git tab's views.
/// </summary>
public sealed class GitPaletteSource : IPaletteSource
{
    private readonly INavigationService _navigation;
    private readonly IGitService _git;
    private readonly ILogger<GitPaletteSource> _logger;

    public GitPaletteSource(INavigationService navigation, IGitService git, ILogger<GitPaletteSource> logger)
    {
        _navigation = navigation;
        _git = git;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_navigation.CurrentPage is not ProjectWorkspaceViewModel workspace
            || !workspace.IsGitRepository
            || workspace.Context.GitStatus is not { } status
            || !workspace.AvailableTabs.Any(t => t.Section == WorkspaceSection.Git))
        {
            return [];
        }

        var items = new List<PaletteItem>();
        var project = workspace.Name;
        if (query.Wants(PaletteCategory.Action))
        {
            var staged = status.Staged.Count();
            items.Add(new PaletteItem
            {
                Title = "Commit staged changes",
                Subtitle = staged > 0 ? $"{project} · {Format.Count(staged, "staged file")}" : $"{project} · nothing staged yet",
                Icon = "Checkmark20",
                Category = PaletteCategory.Action,
                Shortcut = "Ctrl+Enter",
                Keywords = "git commit message save changes",
                Boost = staged > 0 ? 3 : 1,
                Execute = () => workspace.SelectSectionAsync(WorkspaceSection.Git, GitNavigation.Commit()),
            });

            if (!status.IsClean && !status.IsUnborn)
            {
                items.Add(new PaletteItem
                {
                    Title = "Stash changes…",
                    Subtitle = $"{project} · {Format.Count(status.Entries.Count, "changed file")}",
                    Icon = "Archive20",
                    Category = PaletteCategory.Action,
                    Keywords = "git stash shelve save aside wip",
                    Boost = 1,
                    Execute = () => workspace.SelectSectionAsync(WorkspaceSection.Git, GitNavigation.StashChanges()),
                });
            }
        }

        if (query.Wants(PaletteCategory.Navigation))
        {
            items.Add(Navigate(workspace, "Git history", "History20", "git log commits history graph", GitNavigation.History(), boost: 1));
            items.Add(Navigate(workspace, "Git branches", "BranchFork20", "git branches merge delete rename", GitNavigation.Branches()));
            items.Add(Navigate(workspace, "Git stashes", "Archive20", "git stash list pop drop", GitNavigation.Stashes()));
            items.Add(Navigate(workspace, "Git tags", "Tag20", "git tags versions release", GitNavigation.Tags()));
        }

        if (query.Wants(PaletteCategory.Branch))
        {
            items.AddRange(await BranchItemsAsync(workspace, cancellationToken).ConfigureAwait(false));
        }

        return items;
    }

    private async Task<IReadOnlyList<PaletteItem>> BranchItemsAsync(ProjectWorkspaceViewModel workspace, CancellationToken cancellationToken)
    {
        IReadOnlyList<GitBranch> branches;
        try
        {
            branches = await _git.GetBranchesAsync(workspace.Context.Root, includeRemote: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list branches for the palette");
            return [];
        }

        var project = workspace.Name;
        return branches.Where(b => !b.IsRemote && !b.IsCurrent)
            .OrderByDescending(b => b.TipDate ?? DateTimeOffset.MinValue)
            .Select((branch, index) => new PaletteItem
            {
                Title = $"Switch to branch {branch.Name}",
                Subtitle = branch.TipSubject is { Length: > 0 } subject ? $"{project} · {subject}" : project,
                Icon = "BranchFork20",
                Category = PaletteCategory.Branch,
                Keywords = $"git checkout switch {branch.Name}",
                // Recently used branches first.
                Boost = Math.Max(0, 2 - (index * 0.25)),
                Execute = () => SwitchAsync(workspace, branch),
            })
            .ToList();
    }

    /// <summary>Switches through the header's branch selector, which offers to stash changes that block the switch.</summary>
    private static Task SwitchAsync(ProjectWorkspaceViewModel workspace, GitBranch branch)
    {
        var item = new BranchItemViewModel(branch);
        var command = workspace.Branches.CheckoutCommand;
        return command.CanExecute(item) ? command.ExecuteAsync(item) : Task.CompletedTask;
    }

    private static PaletteItem Navigate(ProjectWorkspaceViewModel workspace, string title, string icon, string keywords, GitNavigation navigation,
        double boost = 0) => new()
    {
        Title = title,
        Subtitle = workspace.Name,
        Icon = icon,
        Category = PaletteCategory.Navigation,
        Keywords = keywords,
        Boost = boost,
        Execute = () => workspace.SelectSectionAsync(WorkspaceSection.Git, navigation),
    };
}
