using System.Globalization;
using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Git;

/// <summary>Parsers for for-each-ref, stash list, remote -v, ls-files and numstat/name-status outputs.</summary>
internal static partial class GitRefParsers
{
    public const string HeadsPrefix = "refs/heads/";
    public const string RemotesPrefix = "refs/remotes/";
    public const string TagsPrefix = "refs/tags/";

    public const string BranchFormat =
        "--format=%(refname)%1f%(HEAD)%1f%(upstream)%1f%(upstream:track,nobracket)%1f%(objectname)%1f%(committerdate:iso-strict)%1f%(authorname)%1f%(symref)%1f%(contents:subject)";

    // %(contents) is the raw message (the subject atom would join its first lines); a signature block,
    // when present, is cut off using %(contents:signature).
    public const string TagFormat =
        "--format=%(refname)%1f%(objecttype)%1f%(objectname)%1f%(*objectname)%1f%(creatordate:iso-strict)%1f%(contents:signature)%1f%(contents)%1e";

    public const string StashFormat = "--format=%gd%x1f%cI%x1f%gs";

    public static IReadOnlyList<GitBranch> ParseBranches(string output, IReadOnlyCollection<string> remoteNames)
    {
        var branches = new List<GitBranch>();
        foreach (var line in Lines(output))
        {
            var fields = line.Split('\x1f');
            if (fields.Length < 9)
            {
                continue;
            }

            var fullName = fields[0];
            var isRemote = fullName.StartsWith(RemotesPrefix, StringComparison.Ordinal);
            // refs/remotes/origin/HEAD is a pointer to the default branch, not a branch.
            if (fields[7].Length > 0 || (isRemote && fullName.EndsWith("/HEAD", StringComparison.Ordinal)))
            {
                continue;
            }

            var name = ShortName(fullName);
            var (ahead, behind, gone) = ParseTrack(fields[3]);
            branches.Add(new GitBranch
            {
                Name = name,
                FullName = fullName,
                IsRemote = isRemote,
                IsCurrent = fields[1] == "*",
                RemoteName = isRemote ? RemoteOf(name, remoteNames) : null,
                Upstream = fields[2].Length > 0 ? ShortName(fields[2]) : null,
                UpstreamGone = gone,
                Ahead = ahead,
                Behind = behind,
                TipSha = fields[4].Length > 0 ? fields[4] : null,
                TipDate = fields[5].Length > 0 ? GitLogParser.ParseDate(fields[5]) : null,
                TipAuthor = fields[6].Length > 0 ? fields[6] : null,
                TipSubject = string.Join('\x1f', fields[8..]),
            });
        }

        return branches;
    }

    /// <summary>"ahead 2, behind 1", "behind 3", "gone" or "" (in sync / no upstream).</summary>
    public static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
    {
        if (track.Trim() == "gone")
        {
            return (0, 0, true);
        }

        int ahead = 0, behind = 0;
        foreach (Match match in TrackCount().Matches(track))
        {
            var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
            if (match.Groups["kind"].Value == "ahead")
            {
                ahead = count;
            }
            else
            {
                behind = count;
            }
        }

        return (ahead, behind, false);
    }

    /// <summary>"refs/heads/main" → "main", "refs/remotes/origin/main" → "origin/main", "refs/tags/v1" → "v1".</summary>
    public static string ShortName(string fullName)
    {
        foreach (var prefix in new[] { HeadsPrefix, RemotesPrefix, TagsPrefix })
        {
            if (fullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return fullName[prefix.Length..];
            }
        }

        return fullName;
    }

    /// <summary>The remote a remote-tracking branch belongs to; remote names may themselves contain '/'.</summary>
    public static string? RemoteOf(string remoteBranch, IReadOnlyCollection<string> remoteNames)
    {
        var best = remoteNames
            .Where(r => remoteBranch.StartsWith(r + "/", StringComparison.Ordinal))
            .MaxBy(r => r.Length);
        if (best is not null)
        {
            return best;
        }

        var slash = remoteBranch.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? remoteBranch[..slash] : null;
    }

    public static IReadOnlyList<GitTag> ParseTags(string output)
    {
        var tags = new List<GitTag>();
        foreach (var record in output.Split('\x1e'))
        {
            var fields = record.TrimStart('\n', '\r').Split('\x1f');
            if (fields.Length < 7 || !fields[0].StartsWith(TagsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var annotated = fields[1] == "tag";
            string? message = null;
            if (annotated)
            {
                var signature = fields[5];
                var contents = string.Join('\x1f', fields[6..]);
                if (signature.Length > 0 && contents.EndsWith(signature, StringComparison.Ordinal))
                {
                    contents = contents[..^signature.Length];
                }

                message = contents.Trim();
            }
            tags.Add(new GitTag(
                ShortName(fields[0]),
                annotated && fields[3].Length > 0 ? fields[3] : fields[2],
                fields[4].Length > 0 ? GitLogParser.ParseDate(fields[4]) : null,
                string.IsNullOrEmpty(message) ? null : message,
                annotated));
        }

        return tags;
    }

    public static IReadOnlyList<GitStash> ParseStashes(string output)
    {
        var stashes = new List<GitStash>();
        foreach (var line in Lines(output))
        {
            var fields = line.Split('\x1f', 3);
            if (fields.Length < 3 || StashIndex().Match(fields[0]) is not { Success: true } index)
            {
                continue;
            }

            stashes.Add(new GitStash(
                int.Parse(index.Groups["index"].Value, CultureInfo.InvariantCulture),
                fields[0],
                fields[2],
                fields[1].Length > 0 ? GitLogParser.ParseDate(fields[1]) : null));
        }

        return stashes;
    }

    /// <summary>"origin\thttps://…/r.git (fetch)" lines → one remote per name, in the order git lists them.</summary>
    public static IReadOnlyList<GitRemote> ParseRemotes(string output)
    {
        var order = new List<string>();
        var fetch = new Dictionary<string, string>(StringComparer.Ordinal);
        var push = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var match = RemoteLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            if (!order.Contains(name, StringComparer.Ordinal))
            {
                order.Add(name);
            }

            (match.Groups["kind"].Value == "push" ? push : fetch)[name] = match.Groups["url"].Value;
        }

        return order
            .Select(name =>
            {
                var fetchUrl = fetch.GetValueOrDefault(name) ?? push.GetValueOrDefault(name) ?? string.Empty;
                return new GitRemote(name, fetchUrl, push.GetValueOrDefault(name) ?? fetchUrl);
            })
            .ToList();
    }

    /// <summary>ls-files output without -z: one path per line, C-quoted when it has special characters.</summary>
    public static IReadOnlyList<string> ParsePathLines(string output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var line in Lines(output))
        {
            if (line == GitCli.OutputTruncatedMarker)
            {
                continue;
            }

            var path = GitQuoting.Unquote(line);
            if (seen.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>Joins "diff-tree --numstat -z" and "--name-status -z" outputs (same file order) into file changes.</summary>
    public static IReadOnlyList<GitFileChange> ParseFileChanges(string numstat, string nameStatus)
    {
        var stats = ParseNumstat(numstat);
        var changes = new List<GitFileChange>();
        foreach (var (status, path, oldPath) in ParseNameStatus(nameStatus))
        {
            stats.TryGetValue(path, out var stat);
            changes.Add(new GitFileChange(path, oldPath, MapChangeStatus(status), stat.Additions, stat.Deletions, stat.IsBinary));
        }

        return changes;
    }

    public static IReadOnlyList<(string Status, string Path, string? OldPath)> ParseNameStatus(string output)
    {
        var tokens = Tokens(output);
        var entries = new List<(string, string, string?)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var status = tokens[i];
            if (status.Length == 0)
            {
                continue;
            }

            if (status[0] is 'R' or 'C')
            {
                if (i + 2 >= tokens.Count)
                {
                    break;
                }

                entries.Add((status, tokens[i + 2], tokens[i + 1]));
                i += 2;
            }
            else if (i + 1 < tokens.Count)
            {
                entries.Add((status, tokens[i + 1], null));
                i++;
            }
        }

        return entries;
    }

    public static GitFileState MapChangeStatus(string status) => status.Length == 0 ? GitFileState.Modified : status[0] switch
    {
        'A' => GitFileState.Added,
        'D' => GitFileState.Deleted,
        'R' => GitFileState.Renamed,
        'C' => GitFileState.Copied,
        'T' => GitFileState.TypeChanged,
        'U' => GitFileState.Conflicted,
        _ => GitFileState.Modified,
    };

    private static Dictionary<string, (int Additions, int Deletions, bool IsBinary)> ParseNumstat(string output)
    {
        var tokens = Tokens(output);
        var stats = new Dictionary<string, (int, int, bool)>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var parts = tokens[i].Split('\t', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            var path = parts[2];
            if (path.Length == 0 && i + 2 < tokens.Count)
            {
                // Rename: "added\tdeleted\t\0old\0new\0".
                path = tokens[i + 2];
                i += 2;
            }

            var binary = parts[0] == "-" && parts[1] == "-";
            stats[path] = (binary ? 0 : ParseCount(parts[0]), binary ? 0 : ParseCount(parts[1]), binary);
        }

        return stats;
    }

    private static int ParseCount(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;

    /// <summary>NUL-separated tokens; drops the trailing newline the process runner appends.</summary>
    private static List<string> Tokens(string output)
    {
        var tokens = output.Split('\0').ToList();
        if (tokens.Count > 0 && tokens[^1].Trim().Length == 0)
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return tokens;
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);

    [GeneratedRegex(@"(?<kind>ahead|behind) (?<count>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TrackCount();

    [GeneratedRegex(@"^stash@\{(?<index>\d+)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex StashIndex();

    [GeneratedRegex(@"^(?<name>\S+)\t(?<url>.+) \((?<kind>fetch|push)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex RemoteLine();
}
