using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Tests.Workspace.Support;

namespace ForgeDesk.Presentation.Tests.Dashboard;

internal static class DashboardTestData
{
    public static Project Project(string name, bool pinned = false, string? group = null, DateTimeOffset? lastOpened = null, DateTimeOffset? added = null) =>
        TestData.Project(name, $"/dev/{name}", pinned: pinned, lastOpened: lastOpened, added: added) with { Group = group };

    public static ProjectSnapshot Snapshot(Project project, DateTimeOffset? capturedAt = null, string? branch = "main", int ahead = 0, int behind = 0,
        int changed = 0, bool folderExists = true, bool isGit = true, CiState? ci = null, int openTasks = 0, string? language = null,
        IReadOnlyList<string>? technologies = null, string? problem = null, params AttentionReason[] attention) => new()
    {
        ProjectId = project.Id,
        CapturedAt = capturedAt ?? TestData.Now,
        FolderExists = folderExists,
        IsGitRepository = isGit,
        Branch = branch,
        Upstream = branch is null ? null : $"origin/{branch}",
        Ahead = ahead,
        Behind = behind,
        ChangedFiles = changed,
        Ci = ci is { } state ? new CiSummary { State = state, Branch = branch } : null,
        OpenWorkItems = openTasks,
        PrimaryLanguage = language,
        Technologies = technologies ?? [],
        Problem = problem,
        Attention = attention,
        LastCommitAt = TestData.Now.AddHours(-2),
        LastCommitSubject = "Fix the build",
    };

    public static AttentionReason Critical(string message, string? section = "Git") => new(AttentionLevel.Critical, message, section);

    public static AttentionReason Warning(string message, string? section = "Git") => new(AttentionLevel.Warning, message, section);

    public static AttentionReason Info(string message, string? section = "Git") => new(AttentionLevel.Info, message, section);
}
