namespace ForgeDesk.Core.Files;

/// <summary>Dependency, build-output and tool-state folders that are never indexed or searched by walking.</summary>
internal static class HeavyFolders
{
    // Case-insensitive on every platform: "Bin" and "OBJ" are just as heavy.
    private static readonly HashSet<string> NameSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        ".git",
        "bin",
        "obj",
        "target",
        "dist",
        "build",
        ".venv",
        "venv",
        "__pycache__",
        ".next",
        ".nuxt",
        "packages",
        ".gradle",
        ".idea",
        ".vs",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> SpanLookup =
        NameSet.GetAlternateLookup<ReadOnlySpan<char>>();

    public static IReadOnlySet<string> Names => NameSet;

    public static bool IsHeavy(string? name) => !string.IsNullOrEmpty(name) && NameSet.Contains(name);

    public static bool IsHeavy(ReadOnlySpan<char> name) => !name.IsEmpty && SpanLookup.Contains(name);

    /// <summary>True when any folder segment of a relative path ("a/node_modules/b.js") is heavy.</summary>
    public static bool ContainsHeavySegment(string relativePath)
    {
        var span = relativePath.AsSpan();
        while (true)
        {
            var separator = span.IndexOfAny('/', '\\');
            if (separator < 0)
            {
                // The last segment is the file itself.
                return false;
            }

            if (IsHeavy(span[..separator]))
            {
                return true;
            }

            span = span[(separator + 1)..];
        }
    }
}
