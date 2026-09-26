using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Shell;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Palette;

/// <summary>
/// Application actions (add, clone, theme, GitHub sign-in) and the actions of the open project
/// (fetch, pull, push, branches, "Open in…").
/// </summary>
public sealed class ShellActionsPaletteSource : IPaletteSource
{
    private readonly IProjectActions _actions;
    private readonly INavigationService _navigation;
    private readonly IGitHubAccountService _github;
    private readonly IThemeSwitcher? _theme;

    public ShellActionsPaletteSource(IProjectActions actions, INavigationService navigation, IGitHubAccountService github, IEnumerable<IThemeSwitcher> themes)
    {
        ArgumentNullException.ThrowIfNull(themes);
        _actions = actions;
        _navigation = navigation;
        _github = github;
        _theme = themes.LastOrDefault();
    }

    public Task<IReadOnlyList<PaletteItem>> GetItemsAsync(PaletteQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.Wants(PaletteCategory.Action))
        {
            return Task.FromResult<IReadOnlyList<PaletteItem>>([]);
        }

        var items = new List<PaletteItem>
        {
            Action("Add local project…", "FolderAdd20", "open folder import register existing", _actions.AddLocalProjectAsync, shortcut: "Ctrl+O"),
        };

        if (_actions.CanClone)
        {
            items.Add(Action("Clone repository…", "CloudArrowDown20", "git clone github download checkout", _actions.CloneRepositoryAsync,
                shortcut: "Ctrl+Shift+O"));
        }

        if (_theme is { } theme)
        {
            items.Add(theme.IsDark
                ? Action("Switch to light theme", "WeatherSunny20", "toggle theme appearance mode light dark", theme.ToggleAsync)
                : Action("Switch to dark theme", "WeatherMoon20", "toggle theme appearance mode light dark", theme.ToggleAsync));
        }

        items.Add(_github.Current is { } account
            ? Action("GitHub account", "Person20", "github sign out account token", OpenGitHubSettings, subtitle: $"Signed in as {account.User.Login}")
            : Action("Sign in to GitHub", "Person20", "github login account token authenticate", OpenGitHubSettings));

        if (_navigation.CurrentPage is ProjectWorkspaceViewModel workspace)
        {
            AddProjectActions(items, workspace);
        }

        return Task.FromResult<IReadOnlyList<PaletteItem>>(items);
    }

    private void AddProjectActions(List<PaletteItem> items, ProjectWorkspaceViewModel workspace)
    {
        var name = workspace.Name;
        if (workspace.IsGitRepository)
        {
            var sync = workspace.Sync;
            items.Add(Action("Fetch", "ArrowSync20", "git fetch remote update refresh", () => Run(sync.FetchCommand), name, boost: 2));
            items.Add(Action("Pull", "ArrowDown20", "git pull sync update download", () => Run(sync.PullCommand),
                sync.Behind > 0 ? $"{name} · {Format.Count(sync.Behind, "commit")} to pull" : name, boost: 2));
            items.Add(Action(sync.NeedsPublish ? "Publish branch" : "Push", "ArrowUp20", "git push sync upload publish", () => Run(sync.PushCommand),
                sync.Ahead > 0 && !sync.NeedsPublish ? $"{name} · {Format.Count(sync.Ahead, "commit")} to push" : name, boost: 2));
            items.Add(Action("Create branch…", "BranchFork20", "git new branch checkout", () => Run(workspace.Branches.CreateBranchCommand), name, boost: 1));
            items.Add(Action("Switch branch…", "ArrowSwap20", "git checkout branch change", () =>
            {
                workspace.Branches.IsOpen = true;
                return Task.CompletedTask;
            }, name, boost: 1));
        }

        items.Add(Action("Open in Explorer", "FolderOpen20", "reveal folder file explorer", () => Run(workspace.OpenFolderCommand), name));
        if (workspace.EditorName is { } editor)
        {
            items.Add(Action($"Open in {editor}", "Code20", "editor vscode code ide", () => Run(workspace.OpenInEditorCommand), name));
        }

        items.Add(Action("Open in terminal", "WindowConsole20", "terminal shell console powershell cmd", () => Run(workspace.OpenTerminalCommand), name));
        if (workspace.HasGitHub)
        {
            items.Add(Action("Open on GitHub", "Globe20", "browser repository web github", () => Run(workspace.OpenGitHubCommand), name));
        }

        items.Add(Action("Copy project path", "Copy20", "clipboard folder location", () => Run(workspace.CopyPathCommand), name));
        items.Add(Action("Close project", "Dismiss20", "close workspace", () =>
        {
            _navigation.CloseProject(workspace.ProjectId);
            return Task.CompletedTask;
        }, name, boost: -1));
        items.Add(Action("Remove from ForgeDesk…", "Delete20", "remove forget unregister project", () => _actions.RemoveAsync(workspace.ProjectId), name,
            boost: -2));
    }

    private Task OpenGitHubSettings()
    {
        _navigation.OpenSettings("GitHub");
        return Task.CompletedTask;
    }

    private static Task Run(IRelayCommand command)
    {
        if (!command.CanExecute(null))
        {
            return Task.CompletedTask;
        }

        if (command is IAsyncRelayCommand asyncCommand)
        {
            return asyncCommand.ExecuteAsync(null);
        }

        command.Execute(null);
        return Task.CompletedTask;
    }

    private static PaletteItem Action(string title, string icon, string keywords, Func<Task> execute,
        string? subtitle = null, string? shortcut = null, double boost = 0) => new()
    {
        Title = title,
        Subtitle = subtitle,
        Icon = icon,
        Category = PaletteCategory.Action,
        Keywords = keywords,
        Shortcut = shortcut,
        Boost = boost,
        Execute = execute,
    };
}
