namespace ForgeDesk.Presentation.Git;

/// <summary>The views of the Git tab, in the order of its sub-navigation.</summary>
public enum GitView
{
    Changes,
    History,
    Branches,
    Stashes,
    Tags,
}

/// <summary>
/// Deep link into the Git tab, passed as the argument of
/// <c>ProjectContext.RequestNavigation(WorkspaceSection.Git, …)</c> or
/// <c>INavigationService.OpenProjectAsync(id, WorkspaceSection.Git, …)</c>. The Git tab also accepts a
/// plain string: a commit SHA (7 to 40 hex digits) opens it in History, anything else is read as a
/// repository-relative path (selected in Changes when it has changes, otherwise its history is shown).
/// </summary>
public sealed record GitNavigation(GitView View)
{
    /// <summary>Changes: the file to select. History: only commits touching this path.</summary>
    public string? Path { get; init; }

    /// <summary>History: the commit to select (full or abbreviated SHA).</summary>
    public string? Sha { get; init; }

    /// <summary>History: walk this branch or revision instead of HEAD. Branches / Tags: the item to select.</summary>
    public string? Name { get; init; }

    /// <summary>Changes: focus the commit summary box.</summary>
    public bool FocusCommitMessage { get; init; }

    /// <summary>Changes: prefill the commit message when the box is empty (e.g. after a merge with conflicts).</summary>
    public string? CommitMessage { get; init; }

    /// <summary>Stashes: open the "Stash changes" dialog.</summary>
    public bool StartStash { get; init; }

    public static GitNavigation Changes(string? path = null) => new(GitView.Changes) { Path = path };

    /// <summary>Changes, with the keyboard focus in the commit summary.</summary>
    public static GitNavigation Commit() => new(GitView.Changes) { FocusCommitMessage = true };

    public static GitNavigation History(string? sha = null) => new(GitView.History) { Sha = sha };

    public static GitNavigation FileHistory(string path) => new(GitView.History) { Path = path };

    public static GitNavigation BranchHistory(string branch) => new(GitView.History) { Name = branch };

    public static GitNavigation Branches(string? branch = null) => new(GitView.Branches) { Name = branch };

    public static GitNavigation Stashes() => new(GitView.Stashes);

    public static GitNavigation StashChanges() => new(GitView.Stashes) { StartStash = true };

    public static GitNavigation Tags(string? tag = null) => new(GitView.Tags) { Name = tag };

    /// <summary>
    /// Reads the argument of a navigation request: a <see cref="GitNavigation"/>, a commit SHA or a
    /// path. Returns null for anything else.
    /// </summary>
    public static GitNavigation? From(object? argument) => argument switch
    {
        GitNavigation navigation => navigation,
        GitView view => new GitNavigation(view),
        string text when LooksLikeSha(text) => History(text.Trim()),
        string text when !string.IsNullOrWhiteSpace(text) => new GitNavigation(GitView.Changes) { Path = NormalizePath(text) },
        _ => null,
    };

    /// <summary>True for 7 to 40 hexadecimal digits (an abbreviated or full commit SHA).</summary>
    public static bool LooksLikeSha(string? text)
    {
        var value = text?.Trim();
        return value is { Length: >= 7 and <= 40 } && value.All(char.IsAsciiHexDigit);
    }

    internal static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimStart('/');
}
