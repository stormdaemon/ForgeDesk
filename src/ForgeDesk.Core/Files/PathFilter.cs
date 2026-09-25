using System.Text;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Files;

/// <summary>
/// The "files to include" box of content search: comma- or semicolon-separated globs such as
/// "*.cs, src/**, !*.min.js". A pattern without "/" matches at any depth ("*.cs" finds
/// "a/b/c.cs"), a pattern naming a folder also matches everything inside it, and "!" excludes.
/// The same rules drive git pathspecs and the managed matcher so both engines agree.
/// Matching is case-insensitive on Windows.
/// </summary>
internal sealed class PathFilter
{
    private readonly Regex[] _includes;
    private readonly Regex[] _excludes;

    private PathFilter(IReadOnlyList<string> includeGlobs, IReadOnlyList<string> excludeGlobs, bool ignoreCase)
    {
        IncludeGlobs = includeGlobs;
        ExcludeGlobs = excludeGlobs;
        IgnoreCase = ignoreCase;
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        _includes = includeGlobs.Select(g => new Regex(ToRegex(g), options)).ToArray();
        _excludes = excludeGlobs.Select(g => new Regex(ToRegex(g), options)).ToArray();
    }

    public static PathFilter Everything { get; } = new([], [], OperatingSystem.IsWindows());

    /// <summary>Root-relative globs (git wildmatch syntax) that select files.</summary>
    public IReadOnlyList<string> IncludeGlobs { get; }

    public IReadOnlyList<string> ExcludeGlobs { get; }

    public bool IgnoreCase { get; }

    public bool IsEmpty => IncludeGlobs.Count == 0 && ExcludeGlobs.Count == 0;

    public static PathFilter Parse(string? filter, bool? ignoreCase = null)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return Everything;
        }

        var includes = new List<string>();
        var excludes = new List<string>();
        foreach (var raw in filter.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var exclude = raw.StartsWith('!');
            var pattern = (exclude ? raw[1..] : raw).Trim().Replace('\\', '/');
            if (pattern.StartsWith("./", StringComparison.Ordinal))
            {
                pattern = pattern[2..];
            }

            var anchored = pattern.StartsWith('/') || pattern.TrimEnd('/').Contains('/', StringComparison.Ordinal);
            pattern = pattern.Trim('/');
            if (pattern.Length == 0)
            {
                continue;
            }

            if (!anchored && !pattern.StartsWith("**", StringComparison.Ordinal))
            {
                pattern = "**/" + pattern;
            }

            var target = exclude ? excludes : includes;
            target.Add(pattern);
            if (!pattern.EndsWith("/**", StringComparison.Ordinal) && pattern != "**")
            {
                // A folder pattern also selects the folder's content.
                target.Add(pattern + "/**");
            }
        }

        return new PathFilter(includes, excludes, ignoreCase ?? OperatingSystem.IsWindows());
    }

    /// <summary>Does a root-relative path (forward slashes) pass the filter?</summary>
    public bool IsMatch(string relativePath)
    {
        if (_includes.Length > 0 && !_includes.Any(r => r.IsMatch(relativePath)))
        {
            return false;
        }

        return !_excludes.Any(r => r.IsMatch(relativePath));
    }

    /// <summary>Git pathspecs equivalent to this filter.</summary>
    public IEnumerable<string> ToGitPathspecs()
    {
        var magic = IgnoreCase ? "glob,icase" : "glob";
        foreach (var glob in IncludeGlobs)
        {
            yield return $":({magic}){glob}";
        }

        foreach (var glob in ExcludeGlobs)
        {
            yield return $":(exclude,{magic}){glob}";
        }
    }

    /// <summary>Translates a git wildmatch glob (with "**") to an anchored regular expression.</summary>
    internal static string ToRegex(string glob)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    var atSegmentStart = i == 0 || glob[i - 1] == '/';
                    var followedBySlash = i + 2 < glob.Length && glob[i + 2] == '/';
                    if (atSegmentStart && followedBySlash)
                    {
                        builder.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        i++;
                    }

                    break;
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '[':
                    var close = glob.IndexOf(']', i + 2);
                    if (close < 0)
                    {
                        builder.Append(@"\[");
                        break;
                    }

                    var set = glob[(i + 1)..close];
                    if (set.StartsWith('!'))
                    {
                        set = "^" + set[1..];
                    }

                    builder.Append('[').Append(set.Replace(@"\", @"\\", StringComparison.Ordinal)).Append(']');
                    i = close;
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }
}
