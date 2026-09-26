using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Tests.Workspace.Support;

/// <summary>A workspace section that records how the workspace drives it.</summary>
public sealed class FakeSection : IWorkspaceSectionViewModel, INavigationTarget, IRefreshable, IDisposable
{
    public FakeSection(WorkspaceSection section, ProjectContext context)
    {
        Section = section;
        Context = context;
        RefreshCommand = new AsyncRelayCommand(() =>
        {
            RefreshCount++;
            return Task.CompletedTask;
        });
    }

    public WorkspaceSection Section { get; }

    public ProjectContext Context { get; }

    public int ActivateCount { get; private set; }

    public int DeactivateCount { get; private set; }

    public int RefreshCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public List<object> NavigatedTo { get; } = [];

    public Exception? ActivationFailure { get; set; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public Task ActivateAsync()
    {
        ActivateCount++;
        return ActivationFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public void Deactivate() => DeactivateCount++;

    public Task NavigateToAsync(object argument)
    {
        NavigatedTo.Add(argument);
        return Task.CompletedTask;
    }

    public void Dispose() => IsDisposed = true;
}

/// <summary>Creates <see cref="FakeSection"/>s for the registered sections only.</summary>
public sealed class FakeSectionFactory : IWorkspaceSectionFactory
{
    private readonly HashSet<WorkspaceSection> _registered;

    public FakeSectionFactory(params WorkspaceSection[] registered) => _registered = [.. registered];

    public List<FakeSection> Created { get; } = [];

    public Func<WorkspaceSection, Exception?> FailCreation { get; set; } = _ => null;

    public bool IsAvailable(WorkspaceSection section) => _registered.Contains(section);

    public IWorkspaceSectionViewModel Create(WorkspaceSection section, ProjectContext context)
    {
        if (!_registered.Contains(section))
        {
            throw new InvalidOperationException($"{section} is not registered.");
        }

        if (FailCreation(section) is { } failure)
        {
            throw failure;
        }

        var created = new FakeSection(section, context);
        Created.Add(created);
        return created;
    }

    public FakeSection Single(WorkspaceSection section) => Created.Single(s => s.Section == section);

    public int CountOf(WorkspaceSection section) => Created.Count(s => s.Section == section);
}
