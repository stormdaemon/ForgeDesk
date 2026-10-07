using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Git;

/// <summary>
/// Git operations started from several views (History, Branches, Tags): each asks what it needs,
/// runs, journals, notifies and signals the repository change. Failures are notified, never thrown.
/// </summary>
internal static class GitFlows
{
    /// <summary>
    /// Asks for a name, creates a branch at <paramref name="startPoint"/> (null = HEAD) and switches to it.
    /// When local changes block the switch, offers to stash them first or to create the branch without switching.
    /// </summary>
    public static async Task<bool> CreateBranchAsync(GitSectionContext section, string? startPoint, string startDescription)
    {
        string? name;
        try
        {
            var existing = await section.Git.GetBranchesAsync(section.Root, includeRemote: false, section.Lifetime).ConfigureAwait(true);
            var names = existing.Select(b => b.Name).ToArray();
            name = await section.Dialogs.PromptAsync(new PromptOptions
            {
                Title = "Create branch",
                Message = $"The new branch starts at {startDescription} and becomes the current branch.",
                Placeholder = "feature/short-description",
                ConfirmText = "Create branch",
                Validate = value => BranchNames.Validate(value, names),
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Report(section, ex, "Could not create the branch");
            return false;
        }

        var branch = name?.Trim();
        if (string.IsNullOrEmpty(branch))
        {
            return false;
        }

        try
        {
            var switched = true;
            try
            {
                await section.Git.CreateBranchAsync(section.Root, branch, startPoint, checkout: true, section.Lifetime).ConfigureAwait(true);
            }
            catch (ForgeException ex) when (ex.Kind == ErrorKind.DirtyWorkingTree)
            {
                var choice = await section.Dialogs.ConfirmWithCheckboxAsync(new ConfirmOptions
                {
                    Title = "Uncommitted changes",
                    Message = $"Switching to {branch} would overwrite changes you have not committed.\n\n"
                        + "Stash them to switch now: they stay safe in the Stashes list, ready to be restored.",
                    ConfirmText = "Stash and switch",
                    CheckboxText = "Only create the branch, stay on the current one",
                }).ConfigureAwait(true);
                if (!choice.Confirmed)
                {
                    return false;
                }

                if (choice.CheckboxChecked)
                {
                    await section.Git.CreateBranchAsync(section.Root, branch, startPoint, checkout: false, section.Lifetime).ConfigureAwait(true);
                    switched = false;
                }
                else
                {
                    await StashForSwitchAsync(section, branch).ConfigureAwait(true);
                    await section.Git.CreateBranchAsync(section.Root, branch, startPoint, checkout: true, section.Lifetime).ConfigureAwait(true);
                }
            }

            await section.Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await section.Activity.SucceededAsync(ActivityKind.GitBranchCreated, $"Created branch {branch}", $"From {startDescription}.", branch)
                .ConfigureAwait(true);
            section.Notifications.Show(switched ? $"Created and switched to {branch}" : $"Created {branch}",
                switched ? "Push to publish it when you are ready." : "Switch to it from the Branches list when you are ready.",
                NotificationSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                var error = ErrorInfo.From(ex, "Could not create the branch");
                await section.Activity.FailedAsync(ActivityKind.GitBranchCreated, $"Could not create branch {branch}", error, branch).ConfigureAwait(true);
                section.Notifications.ShowError(error);
            }

            return false;
        }
    }

    /// <summary>Stashes every change (new files included) before a switch that git refused.</summary>
    public static async Task StashForSwitchAsync(GitSectionContext section, string target)
    {
        var current = section.Context.GitStatus?.Branch ?? "HEAD";
        await section.Git.StashAsync(section.Root, $"ForgeDesk: changes on {current} before switching to {target}", includeUntracked: true, section.Lifetime)
            .ConfigureAwait(true);
        await section.Activity.SucceededAsync(ActivityKind.GitStash, $"Stashed changes on {current}", $"Before switching to {target}.",
            section.Context.GitStatus?.Branch).ConfigureAwait(true);
    }

    /// <summary>Asks for a tag name and message, then tags <paramref name="target"/> (null = HEAD).</summary>
    public static async Task<string?> CreateTagAsync(GitSectionContext section, string? target, string targetDescription, IReadOnlyCollection<string>? existingTags = null)
    {
        CreateTagDialogViewModel dialog;
        try
        {
            var existing = existingTags ?? (await section.Git.GetTagsAsync(section.Root, section.Lifetime).ConfigureAwait(true)).Select(t => t.Name).ToArray();
            dialog = new CreateTagDialogViewModel(targetDescription, existing);
            if (await section.Dialogs.ShowDialogAsync(dialog).ConfigureAwait(true) != true)
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Report(section, ex, "Could not create the tag");
            return null;
        }

        var name = dialog.Name.Trim();
        var message = string.IsNullOrWhiteSpace(dialog.Message) ? null : dialog.Message.Trim();
        try
        {
            await section.Git.CreateTagAsync(section.Root, name, message, target, section.Lifetime).ConfigureAwait(true);
            await section.Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            await section.Activity.SucceededAsync(ActivityKind.GitTagCreated, $"Created tag {name}", $"On {targetDescription}.").ConfigureAwait(true);
            section.Notifications.Show($"Created tag {name}", "It exists only in this repository until you push it.", NotificationSeverity.Success,
                new NotificationAction("Push tag", () => PushTagAsync(section, name)));
            return name;
        }
        catch (Exception ex)
        {
            Report(section, ex, "Could not create the tag");
            return null;
        }
    }

    public static async Task<bool> PushTagAsync(GitSectionContext section, string tag)
    {
        try
        {
            await section.Git.PushTagAsync(section.Root, tag, null, section.Lifetime).ConfigureAwait(true);
            await section.Activity.SucceededAsync(ActivityKind.GitPush, $"Pushed tag {tag}").ConfigureAwait(true);
            section.Notifications.Show($"Pushed tag {tag}", null, NotificationSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            if (!ex.IsCancellation())
            {
                var error = ErrorInfo.From(ex, ex is ForgeException { Kind: not ErrorKind.Unknown } ? null : $"Could not push {tag}");
                await section.Activity.FailedAsync(ActivityKind.GitPush, $"Could not push tag {tag}", error).ConfigureAwait(true);
                section.Notifications.ShowError(error);
            }

            return false;
        }
    }

    /// <summary>The commit's page on GitHub, when the project is linked to a GitHub repository.</summary>
    public static string? GitHubCommitUrl(ProjectContext context, string sha) =>
        context.Project.GitHub is { } repo ? $"{repo.HtmlUrl}/commit/{sha}" : null;

    private static void Report(GitSectionContext section, Exception ex, string title)
    {
        if (!ex.IsCancellation())
        {
            section.Notifications.ShowError(ErrorInfo.From(ex, ex is ForgeException { Kind: not ErrorKind.Unknown } ? null : title));
        }
    }
}
