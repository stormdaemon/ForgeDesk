using ForgeDesk.Core.Files;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.WorkItems;
using ForgeDesk.Presentation.Tasks;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Tasks.Support;

/// <summary>A Tasks tab on a real context with substituted services; <see cref="Items"/> is what the service returns.</summary>
public sealed class TasksHarness : IDisposable
{
    private readonly List<TasksSectionViewModel> _sections = [];

    public TasksHarness()
    {
        Workspace = new WorkspaceHarness();
        WorkItems.GetAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<WorkItem>>(Items.ToList()));
        WorkItems.GetHistoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        Workspace.Git.GetBranchesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<GitBranch>>(Branches));
    }

    public WorkspaceHarness Workspace { get; }

    public IWorkItemService WorkItems => Workspace.WorkItems;

    public IGitService Git => Workspace.Git;

    public IFileIndex FileIndex { get; } = Substitute.For<IFileIndex>();

    public List<WorkItem> Items { get; } = [];

    public List<GitBranch> Branches { get; } = [TestData.LocalBranch("main", current: true)];

    public string ProjectId => Workspace.Project.Id;

    public ProjectContext Context { get; private set; } = null!;

    public async Task<TasksSectionViewModel> CreateAsync(bool activate = true)
    {
        Context = Workspace.CreateContext();
        await Context.RefreshGitStatusAsync();
        var section = new TasksSectionViewModel(Context, Workspace.Services, FileIndex) { TextSaveDelay = Timeout.InfiniteTimeSpan };
        _sections.Add(section);
        if (activate)
        {
            await section.ActivateAsync();
        }

        return section;
    }

    public WorkItem Item(int number, string title, WorkItemStatus status = WorkItemStatus.Todo, double sortOrder = 0, WorkItemPriority priority = WorkItemPriority.None,
        IReadOnlyList<string>? labels = null, DateTimeOffset? due = null, DateTimeOffset? completed = null, IReadOnlyList<WorkItemLink>? links = null,
        string description = "") => new()
    {
        Id = $"w{number}",
        ProjectId = ProjectId,
        Number = number,
        Title = title,
        Description = description,
        Status = status,
        Priority = priority,
        Labels = labels ?? [],
        SortOrder = sortOrder == 0 ? number * 1024 : sortOrder,
        CreatedAt = DateTimeOffset.Now.AddDays(-3),
        UpdatedAt = completed ?? DateTimeOffset.Now.AddHours(-number),
        CompletedAt = completed,
        DueAt = due,
        Links = links ?? [],
    };

    /// <summary>Makes the service return <paramref name="updated"/> for the next calls and as part of <see cref="Items"/>.</summary>
    public WorkItem Replace(WorkItem updated)
    {
        var index = Items.FindIndex(i => i.Id == updated.Id);
        if (index >= 0)
        {
            Items[index] = updated;
        }
        else
        {
            Items.Add(updated);
        }

        return updated;
    }

    public void RaiseChanged(string? workItemId = null) =>
        WorkItems.Changed += Raise.Event<EventHandler<WorkItemsChangedEventArgs>>(WorkItems, new WorkItemsChangedEventArgs(ProjectId, workItemId));

    public void Dispose()
    {
        foreach (var section in _sections)
        {
            section.Dispose();
        }

        Workspace.Dispose();
    }
}
