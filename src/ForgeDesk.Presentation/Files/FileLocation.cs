namespace ForgeDesk.Presentation.Files;

/// <summary>
/// A deep link into the Files tab: a project-relative path (forward slashes) and an optional
/// 1-based line. Other features navigate with
/// <c>ProjectContext.RequestNavigation(WorkspaceSection.Files, new FileLocation("src/app.cs", 42))</c>
/// or with a plain relative path string.
/// </summary>
public sealed record FileLocation(string RelativePath, int? Line = null)
{
    /// <summary>Reads a navigation argument: a <see cref="FileLocation"/> or a relative path. Null for anything else.</summary>
    public static FileLocation? From(object? argument) => argument switch
    {
        FileLocation location when Normalize(location.RelativePath) is { } path =>
            location with { RelativePath = path, Line = location.Line is > 0 ? location.Line : null },
        string text when Normalize(text) is { } path => new FileLocation(path),
        _ => null,
    };

    /// <summary>
    /// Normalizes a project-relative path: forward slashes, no leading "./" or "/", no trailing
    /// slash. Returns null for blank input. The project root itself is the empty string.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var value = path.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.Trim('/');
        if (value == ".")
        {
            value = string.Empty;
        }

        return value.Length == 0 && path.Trim().Length == 0 ? null : value;
    }

    /// <summary>"src/app" for "src/app/main.cs", "" for a file at the root.</summary>
    public static string ParentOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    /// <summary>"main.cs" for "src/app/main.cs".</summary>
    public static string NameOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    /// <summary>Ancestor folders, outermost first: "a", "a/b" for "a/b/c.txt".</summary>
    public static IEnumerable<string> AncestorsOf(string relativePath)
    {
        var index = relativePath.IndexOf('/');
        while (index > 0)
        {
            yield return relativePath[..index];
            index = relativePath.IndexOf('/', index + 1);
        }
    }
}
