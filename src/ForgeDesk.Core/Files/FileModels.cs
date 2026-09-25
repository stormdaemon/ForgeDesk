namespace ForgeDesk.Core.Files;

public sealed record FileEntry
{
    public required string Name { get; init; }

    /// <summary>Path relative to the project root, forward slashes.</summary>
    public required string RelativePath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset LastModified { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>Generated or dependency folders (node_modules, bin, obj, target…) shown dimmed and never indexed.</summary>
    public bool IsHeavyFolder { get; init; }

    /// <summary>Matched by .gitignore.</summary>
    public bool IsIgnored { get; init; }

    public bool IsSymbolicLink { get; init; }
}

public enum FileContentKind
{
    Text,
    Image,
    Binary,
}

public sealed record FileContent
{
    public required string RelativePath { get; init; }
    public FileContentKind Kind { get; init; }

    /// <summary>Decoded text for text files (possibly truncated), otherwise null.</summary>
    public string? Text { get; init; }
    public long Size { get; init; }
    public bool IsTruncated { get; init; }
    public string? EncodingName { get; init; }

    /// <summary>"CRLF", "LF" or "Mixed".</summary>
    public string? LineEndings { get; init; }
    public int LineCount { get; init; }
    public DateTimeOffset LastModified { get; init; }
}

public sealed record FileMatch(string RelativePath, double Score, IReadOnlyList<int> MatchedIndices);

public sealed record FileIndexSnapshot(string Root, IReadOnlyList<string> Files, bool IsTruncated, DateTimeOffset BuiltAt);

public sealed record ContentSearchQuery
{
    public required string Pattern { get; init; }
    public bool IsRegex { get; init; }
    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }

    /// <summary>Optional glob filter on paths, e.g. "*.cs" or "src/**".</summary>
    public string? PathFilter { get; init; }
    public int MaxResults { get; init; } = 2000;
}

public sealed record ContentMatch(string RelativePath, int LineNumber, int Column, int Length, string LineText);

public sealed record ContentSearchSummary(int Matches, int FilesMatched, bool IsTruncated, TimeSpan Duration, string Engine);
