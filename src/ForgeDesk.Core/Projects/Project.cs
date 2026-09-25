namespace ForgeDesk.Core.Projects;

/// <summary>Owner/name pair identifying a GitHub repository.</summary>
public sealed record GitHubRepoRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public string HtmlUrl => $"https://github.com/{Owner}/{Name}";

    public override string ToString() => FullName;
}

/// <summary>A project registered in ForgeDesk. Removing it never touches the files.</summary>
public sealed record Project
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Absolute, normalized path of the project root.</summary>
    public required string Path { get; init; }

    public DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset? LastOpenedAt { get; init; }
    public bool IsPinned { get; init; }

    /// <summary>User-defined group ("Work", "Side projects"…); null = ungrouped.</summary>
    public string? Group { get; init; }

    /// <summary>Optional accent color as #RRGGBB used for the project avatar.</summary>
    public string? Color { get; init; }

    public int SortOrder { get; init; }

    /// <summary>GitHub repository linked to the project (from the origin remote), if any.</summary>
    public GitHubRepoRef? GitHub { get; init; }
}
