using ForgeDesk.Core.Activity;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Activity;

public enum ActivityTargetKind
{
    /// <summary>A workspace tab of the entry's project, with an optional argument (SHA, run id, task id).</summary>
    Section,

    /// <summary>A web page (release, cloned repository…).</summary>
    Url,

    /// <summary>The entry's project itself (global activity page).</summary>
    Project,
}

/// <summary>Where clicking an activity entry leads.</summary>
public sealed record ActivityTarget(ActivityTargetKind Kind, string? ProjectId, WorkspaceSection? Section = null, string? Argument = null, string? Url = null)
{
    /// <summary>Short call to action ("Show commit", "Open in browser").</summary>
    public string ActionText => Kind switch
    {
        ActivityTargetKind.Url => "Open in browser",
        ActivityTargetKind.Project => "Open project",
        _ => Section switch
        {
            WorkspaceSection.Git when Argument is not null => "Show commit",
            WorkspaceSection.Git => "Open Git",
            WorkspaceSection.Commands when Argument is not null => "Show run",
            WorkspaceSection.Tasks when Argument is not null => "Open task",
            { } section => $"Open {WorkspaceSectionInfo.For(section).Title}",
            _ => "Open",
        },
    };

    /// <summary>
    /// Resolves what an entry points to. On the project tab (<paramref name="isGlobal"/> false) only
    /// references are followed; on the global page an entry without a reference opens its project.
    /// </summary>
    public static ActivityTarget? Resolve(ActivityEntry entry, bool isGlobal)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var value = entry.RefValue?.Trim();
        var kind = entry.RefKind?.Trim().ToLowerInvariant();
        var projectId = entry.ProjectId;

        if (kind == "url" && IsWebUrl(value))
        {
            return new ActivityTarget(ActivityTargetKind.Url, projectId, Url: value);
        }

        // Section targets need a project: on the global page the entry's own, on the tab the open one.
        var canOpenSection = !isGlobal || projectId is not null;
        if (canOpenSection && !string.IsNullOrEmpty(value))
        {
            var section = kind switch
            {
                "commit" => WorkspaceSection.Git,
                "run" => WorkspaceSection.Commands,
                "work-item" => WorkspaceSection.Tasks,
                _ => (WorkspaceSection?)null,
            };
            if (section is { } target)
            {
                return new ActivityTarget(ActivityTargetKind.Section, projectId, target, value);
            }
        }

        if (canOpenSection)
        {
            var section = kind switch
            {
                "branch" => WorkspaceSection.Git,
                "release" => WorkspaceSection.Releases,
                _ => (WorkspaceSection?)null,
            };
            if (section is { } target)
            {
                return new ActivityTarget(ActivityTargetKind.Section, projectId, target);
            }
        }

        return isGlobal && projectId is not null && entry.Kind != ActivityKind.ProjectRemoved
            ? new ActivityTarget(ActivityTargetKind.Project, projectId)
            : null;
    }

    private static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
