using System.Windows;
using System.Windows.Shell;
using ForgeDesk.App.Activation;
using ForgeDesk.App.Services.Launching;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.App.Services.Windowing;

/// <summary>
/// Keeps the taskbar jump list in sync with pinned and recent projects. Each entry relaunches
/// ForgeDesk with <c>--open-project &lt;id&gt;</c>, which the running instance receives.
/// </summary>
internal sealed class JumpListService : IDisposable
{
    private readonly IProjectRegistry _registry;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<JumpListService> _logger;
    private readonly Debouncer _debouncer = new(TimeSpan.FromSeconds(1));

    public JumpListService(IProjectRegistry registry, IUiDispatcher dispatcher, ILogger<JumpListService> logger)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public void Start()
    {
        _registry.Changed += OnProjectsChanged;
        _debouncer.Trigger(RefreshAsync);
    }

    public void Dispose()
    {
        _registry.Changed -= OnProjectsChanged;
        _debouncer.Dispose();
    }

    private void OnProjectsChanged(object? sender, ProjectsChangedEventArgs e) => _debouncer.Trigger(RefreshAsync);

    private async Task RefreshAsync()
    {
        try
        {
            var projects = await _registry.GetAllAsync().ConfigureAwait(false);
            var entries = JumpListPlanner.Plan(projects);
            await _dispatcher.InvokeAsync(() => Apply(entries)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the jump list");
        }
    }

    private static void Apply(IReadOnlyList<JumpListEntry> entries)
    {
        var application = Application.Current;
        var executable = Environment.ProcessPath;
        if (application is null || string.IsNullOrEmpty(executable))
        {
            return;
        }

        var jumpList = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
        foreach (var entry in entries)
        {
            jumpList.JumpItems.Add(new JumpTask
            {
                Title = entry.Project.Name,
                Description = entry.Project.Path,
                ApplicationPath = executable,
                Arguments = WindowsCommandLine.Join([ActivationRequest.OpenProjectOption, entry.Project.Id]),
                IconResourcePath = executable,
                IconResourceIndex = 0,
                CustomCategory = entry.Category,
            });
        }

        JumpList.SetJumpList(application, jumpList);
        jumpList.Apply();
    }
}
