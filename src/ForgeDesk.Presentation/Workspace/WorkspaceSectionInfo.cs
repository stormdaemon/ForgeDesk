namespace ForgeDesk.Presentation.Workspace;

/// <summary>Display metadata of a workspace tab: label, icon (WPF-UI SymbolRegular name) and search keywords.</summary>
public sealed record WorkspaceSectionInfo(WorkspaceSection Section, string Title, string Icon, string Keywords)
{
    private static readonly Dictionary<WorkspaceSection, WorkspaceSectionInfo> All = new()
    {
        [WorkspaceSection.Overview] = new(WorkspaceSection.Overview, "Overview", "Board20", "summary readme health stack"),
        [WorkspaceSection.Git] = new(WorkspaceSection.Git, "Git", "BranchFork20", "changes commit stage diff history branches stash"),
        [WorkspaceSection.Files] = new(WorkspaceSection.Files, "Files", "Folder20", "explorer tree browse source code"),
        [WorkspaceSection.Terminal] = new(WorkspaceSection.Terminal, "Terminal", "WindowConsole20", "shell console powershell cmd bash"),
        [WorkspaceSection.Commands] = new(WorkspaceSection.Commands, "Commands", "Play20", "run scripts build test dev logs"),
        [WorkspaceSection.Tasks] = new(WorkspaceSection.Tasks, "Tasks", "TaskListSquareLtr20", "todo board kanban work items"),
        [WorkspaceSection.GitHub] = new(WorkspaceSection.GitHub, "GitHub", "Globe20", "issues pull requests actions ci workflows"),
        [WorkspaceSection.Releases] = new(WorkspaceSection.Releases, "Releases", "Rocket20", "publish tag version changelog"),
        [WorkspaceSection.Insights] = new(WorkspaceSection.Insights, "Insights", "DataTrending20", "health analysis languages statistics todo"),
        [WorkspaceSection.Activity] = new(WorkspaceSection.Activity, "Activity", "History20", "journal log timeline history"),
    };

    /// <summary>Every section, in tab order.</summary>
    public static IReadOnlyList<WorkspaceSectionInfo> Ordered { get; } =
        Enum.GetValues<WorkspaceSection>().Select(section => All[section]).ToArray();

    public static WorkspaceSectionInfo For(WorkspaceSection section) => All[section];

    /// <summary>"Ctrl+1" … "Ctrl+9", "Ctrl+0" for the tenth visible tab, null beyond.</summary>
    public static string? ShortcutForPosition(int position) => position switch
    {
        >= 0 and < 9 => $"Ctrl+{position + 1}",
        9 => "Ctrl+0",
        _ => null,
    };
}
