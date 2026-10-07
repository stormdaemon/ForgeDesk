using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Presentation.Infrastructure;

namespace ForgeDesk.Presentation.Git;

/// <summary>The commit box of the Changes view.</summary>
public sealed partial class ChangesViewModel
{
    /// <summary>Summaries longer than this are truncated by most tools (GitHub, git log --oneline).</summary>
    public const int RecommendedSummaryLength = 72;

    private (string Summary, string Description)? _draftBeforeAmend;
    private (string Summary, string Description)? _amendPrefill;
    private bool _amendRewritesPushedCommit;
    private bool _suppressAmendHandler;
    private bool _identityConfirmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryLength), nameof(IsSummaryTooLong), nameof(SummaryCounterText), nameof(HasSummary))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand))]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDescriptionExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText), nameof(CommitToolTip), nameof(CommitAndPushToolTip))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand))]
    public partial bool IsAmend { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitAndPushCommand))]
    public partial bool IsCommitting { get; private set; }

    /// <summary>The commit is done and "Commit &amp; push" is pushing it.</summary>
    [ObservableProperty]
    public partial bool IsPushingAfterCommit { get; private set; }

    /// <summary>Completes when the last Amend toggle has been handled (confirmation, prefill).</summary>
    internal Task AmendToggleTask { get; private set; } = Task.CompletedTask;

    public int SummaryLength => Summary.Trim().Length;

    public bool HasSummary => SummaryLength > 0;

    public bool IsSummaryTooLong => SummaryLength > RecommendedSummaryLength;

    /// <summary>"12/72".</summary>
    public string SummaryCounterText => $"{SummaryLength}/{RecommendedSummaryLength}";

    /// <summary>Amending needs a commit to amend.</summary>
    public bool CanAmend => !IsUnborn;

    /// <summary>"Commit to main", "Commit all (5)" when nothing is staged, "Amend last commit".</summary>
    public string CommitButtonText
    {
        get
        {
            if (IsAmend)
            {
                return "Amend last commit";
            }

            if (StagedCount == 0 && UnstagedCount > 0)
            {
                return $"Commit all ({UnstagedCount})";
            }

            return BranchName is { } branch ? $"Commit to {branch}" : "Commit";
        }
    }

    public string CommitAndPushText => BranchName is not null && Upstream is null ? "Commit & publish" : "Commit & push";

    public string CommitToolTip
    {
        get
        {
            if (ConflictCount > 0)
            {
                return "Resolve and stage the conflicted files before committing";
            }

            var what = IsAmend
                ? "Replace the last commit with the staged changes and this message"
                : StagedCount == 0 && UnstagedCount > 0
                    ? $"Nothing is staged: stage all {Format.Count(UnstagedCount, "change")} and commit them"
                    : $"Commit {Format.Count(StagedCount, "staged change")}";
            return $"{what} (Ctrl+Enter)";
        }
    }

    public string CommitAndPushToolTip
    {
        get
        {
            if (BranchName is null)
            {
                return "You're not on a branch, so there is nothing to push to";
            }

            if (IsAmend && _amendRewritesPushedCommit)
            {
                return "The amended commit replaces one that is already pushed: commit, then force push from a terminal";
            }

            var push = Upstream is null ? $"publish {BranchName}" : $"push to {Upstream}";
            return $"Commit, then {push} (Ctrl+Shift+Enter)";
        }
    }

    partial void OnIsDescriptionExpandedChanged(bool value) => Section.Preferences.CommitDescriptionExpanded = value;

    partial void OnIsAmendChanged(bool value)
    {
        if (_suppressAmendHandler)
        {
            return;
        }

        AmendToggleTask = value ? EnterAmendAsync() : ExitAmend();
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task CommitAsync() => CommitCoreAsync(push: false);

    [RelayCommand(CanExecute = nameof(CanCommitAndPush))]
    private Task CommitAndPushAsync() => CommitCoreAsync(push: true);

    private bool CanCommit() =>
        !IsCommitting && HasSummary && HasStatus && ConflictCount == 0 && (StagedCount > 0 || UnstagedCount > 0 || IsAmend);

    private bool CanCommitAndPush() => CanCommit() && BranchName is not null && !(IsAmend && _amendRewritesPushedCommit);

    private async Task CommitCoreAsync(bool push)
    {
        if (IsCommitting || Context.GitStatus is not { } status)
        {
            return;
        }

        var message = BuildMessage();
        if (message is null)
        {
            Notify("Write a summary first", "The summary is the first line of the commit message.", NotificationSeverity.Warning);
            return;
        }

        IsCommitting = true;
        GitCommit? commit = null;
        try
        {
            if (!await EnsureIdentityAsync().ConfigureAwait(true))
            {
                return;
            }

            var amend = IsAmend;
            var stageAll = false;
            if (!amend && StagedCount == 0)
            {
                if (UnstagedCount == 0)
                {
                    return;
                }

                var confirmed = await Section.Dialogs.ConfirmAsync(new ConfirmOptions
                {
                    Title = $"Commit all {Format.Count(UnstagedCount, "change")}?",
                    Message = "Nothing is staged yet. ForgeDesk will stage every changed file, including new files, and commit them together."
                        + "\n\nTo commit only some files, stage them first.",
                    ConfirmText = "Stage all and commit",
                }).ConfigureAwait(true);
                if (!confirmed)
                {
                    return;
                }

                stageAll = true;
            }

            commit = await Git.CommitAsync(Root, new GitCommitOptions(message, amend, stageAll), Section.Lifetime).ConfigureAwait(true);
            ClearCommitBox();
            await Section.Activity.CommitAsync(amend ? $"Amended commit {commit.ShortSha}" : $"Committed {commit.ShortSha}",
                commit.Subject, commit.Sha).ConfigureAwait(true);
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);

            if (!push)
            {
                Notify($"{(amend ? "Amended" : "Committed")} {commit.ShortSha} · {commit.Subject}",
                    status.Branch is { } branch ? $"On {branch}." : null);
                return;
            }

            IsPushingAfterCommit = true;
            await PushAfterCommitAsync(commit, status).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            // The project was closed while committing.
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, ex is ForgeException { Kind: not ErrorKind.Unknown } ? null : "Commit failed");
            await Section.Activity.FailedAsync(ActivityKind.GitCommit, "Commit failed", error, status.Branch).ConfigureAwait(true);
            Section.Notifications.ShowError(error);
        }
        finally
        {
            IsCommitting = false;
            IsPushingAfterCommit = false;
        }
    }

    private async Task PushAfterCommitAsync(GitCommit commit, GitStatus before)
    {
        var branch = Context.GitStatus?.Branch ?? before.Branch;
        if (branch is null)
        {
            Notify($"Committed {commit.ShortSha} · {commit.Subject}", "You're not on a branch, so nothing was pushed.", NotificationSeverity.Warning);
            return;
        }

        var publish = (Context.GitStatus?.Upstream ?? before.Upstream) is null;
        try
        {
            await Git.PushAsync(Root, new GitPushOptions(SetUpstream: true, Branch: publish ? branch : null), null, Section.Lifetime).ConfigureAwait(true);
            await Context.NotifyRepositoryChangedAsync().ConfigureAwait(true);
            var upstream = Context.GitStatus?.Upstream ?? before.Upstream;
            var title = publish ? $"Committed {commit.ShortSha} and published {branch}" : $"Committed {commit.ShortSha} and pushed to {upstream ?? "the remote"}";
            await Section.Activity.SucceededAsync(ActivityKind.GitPush, publish ? $"Published {branch}" : $"Pushed to {upstream ?? "the remote"}",
                commit.Subject, branch).ConfigureAwait(true);
            Notify(title, commit.Subject);
        }
        catch (Exception ex) when (ex.IsCancellation())
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = ErrorInfo.From(ex, "Push failed");
            await Section.Activity.FailedAsync(ActivityKind.GitPush, "Push failed", error, branch).ConfigureAwait(true);
            Section.Notifications.ShowError(error with
            {
                Title = $"Committed {commit.ShortSha}, but the push failed",
                Hint = error.Hint ?? "The commit is safe on your branch. Push it again from the header when the problem is fixed.",
            });
        }
    }

    /// <summary>Checks that git knows who is committing, asking for a name and an email otherwise.</summary>
    private async Task<bool> EnsureIdentityAsync()
    {
        if (_identityConfirmed)
        {
            return true;
        }

        var identity = await Git.GetIdentityAsync(Root, Section.Lifetime).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(identity.Name) && !string.IsNullOrWhiteSpace(identity.Email))
        {
            _identityConfirmed = true;
            return true;
        }

        var dialog = new GitIdentityDialogViewModel(identity.Name, identity.Email);
        if (await Section.Dialogs.ShowDialogAsync(dialog).ConfigureAwait(true) != true)
        {
            return false;
        }

        var name = dialog.Name.Trim();
        var email = dialog.Email.Trim();
        await Git.SetGlobalIdentityAsync(name, email, Section.Lifetime).ConfigureAwait(true);
        _identityConfirmed = true;
        Notify("Git identity saved", $"Your commits are signed {name} <{email}>.", NotificationSeverity.Info);
        return true;
    }

    private async Task EnterAmendAsync()
    {
        var status = Context.GitStatus;
        if (status is null || status.IsUnborn)
        {
            SetAmend(false);
            return;
        }

        _amendRewritesPushedCommit = status.Upstream is not null && status.Ahead == 0;
        if (_amendRewritesPushedCommit)
        {
            bool confirmed;
            try
            {
                confirmed = await Section.Dialogs.ConfirmAsync(new ConfirmOptions
                {
                    Title = "Amend a pushed commit?",
                    Message = $"The last commit is already on {status.Upstream}. Amending replaces it with a new commit, so the branch "
                        + "must then be force pushed, which rewrites it for everyone who pulled it.",
                    ConfirmText = "Amend anyway",
                    IsDestructive = true,
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (!ex.IsCancellation())
            {
                ShowError(ex, "Could not ask for confirmation");
                confirmed = false;
            }

            if (!confirmed)
            {
                _amendRewritesPushedCommit = false;
                SetAmend(false);
                return;
            }
        }

        OnPropertyChanged(nameof(CommitAndPushToolTip));
        CommitAndPushCommand.NotifyCanExecuteChanged();

        try
        {
            var last = LastCommit ?? (await Git.GetLogAsync(Root, new GitLogQuery { Take = 1 }, Section.Lifetime).ConfigureAwait(true)).FirstOrDefault();
            if (last is null || !IsAmend)
            {
                return;
            }

            _draftBeforeAmend = (Summary, Description);
            Summary = last.Subject;
            Description = last.Body;
            _amendPrefill = (Summary, Description);
            if (last.Body.Length > 0)
            {
                IsDescriptionExpanded = true;
            }
        }
        catch (Exception ex) when (!ex.IsCancellation())
        {
            ShowError(ex, "Could not read the last commit");
        }
        catch (Exception)
        {
            // The project was closed.
        }
    }

    private Task ExitAmend()
    {
        // Give back what was being written before Amend, unless the prefilled message was edited.
        if (_draftBeforeAmend is { } draft && _amendPrefill is { } prefill && Summary == prefill.Summary && Description == prefill.Description)
        {
            Summary = draft.Summary;
            Description = draft.Description;
        }

        _draftBeforeAmend = null;
        _amendPrefill = null;
        _amendRewritesPushedCommit = false;
        OnPropertyChanged(nameof(CommitAndPushToolTip));
        CommitAndPushCommand.NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    private void SetAmend(bool value)
    {
        _suppressAmendHandler = true;
        try
        {
            IsAmend = value;
        }
        finally
        {
            _suppressAmendHandler = false;
        }
    }

    private void ClearCommitBox()
    {
        Summary = string.Empty;
        Description = string.Empty;
        _draftBeforeAmend = null;
        _amendPrefill = null;
        _amendRewritesPushedCommit = false;
        SetAmend(false);
    }

    /// <summary>Summary, blank line, description; null without a summary.</summary>
    internal string? BuildMessage()
    {
        var summary = Summary.Trim();
        if (summary.Length == 0)
        {
            return null;
        }

        var description = Description.Trim();
        return description.Length == 0 ? summary : $"{summary}\n\n{description}";
    }

    private static (string Subject, string Body) SplitMessage(string message)
    {
        var text = message.ReplaceLineEndings("\n").Trim();
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? (text, string.Empty) : (text[..newline].Trim(), text[(newline + 1)..].Trim());
    }
}
