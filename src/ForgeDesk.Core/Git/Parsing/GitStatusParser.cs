using System.Globalization;

namespace ForgeDesk.Core.Git;

/// <summary>
/// Parses "git status --porcelain=v2 -z --branch --show-stash". Records are NUL separated; paths are
/// raw (never quoted) and relative to the repository root.
/// </summary>
internal static class GitStatusParser
{
    public static GitStatus Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var records = output.Split('\0');
        var entries = new List<GitStatusEntry>();
        string? branch = null;
        string? headSha = null;
        string? upstream = null;
        bool detached = false, unborn = false;
        int ahead = 0, behind = 0, stashCount = 0;

        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length < 2 || record.Trim().Length == 0)
            {
                continue;
            }

            switch (record[0])
            {
                case '#':
                    ParseHeader(record, ref branch, ref headSha, ref upstream, ref detached, ref unborn, ref ahead, ref behind, ref stashCount);
                    break;
                case '1':
                    AddIfValid(entries, ParseOrdinary(record));
                    break;
                case '2':
                    // The original path of a rename/copy is the next NUL-separated record.
                    var originalPath = i + 1 < records.Length ? records[++i] : null;
                    AddIfValid(entries, ParseRenamed(record, originalPath));
                    break;
                case 'u':
                    AddIfValid(entries, ParseUnmerged(record));
                    break;
                case '?':
                    entries.Add(new GitStatusEntry { Path = record[2..], IndexState = GitFileState.Unmodified, WorkTreeState = GitFileState.Untracked });
                    break;
                case '!':
                    entries.Add(new GitStatusEntry { Path = record[2..], IndexState = GitFileState.Unmodified, WorkTreeState = GitFileState.Ignored });
                    break;
            }
        }

        return new GitStatus
        {
            Branch = detached ? null : branch,
            HeadSha = headSha,
            IsDetached = detached,
            IsUnborn = unborn,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            StashCount = stashCount,
            Entries = entries,
        };
    }

    public static GitFileState MapStatusCode(char code) => code switch
    {
        '.' or ' ' => GitFileState.Unmodified,
        'M' => GitFileState.Modified,
        'T' => GitFileState.TypeChanged,
        'A' => GitFileState.Added,
        'D' => GitFileState.Deleted,
        'R' => GitFileState.Renamed,
        'C' => GitFileState.Copied,
        'U' => GitFileState.Conflicted,
        '?' => GitFileState.Untracked,
        '!' => GitFileState.Ignored,
        _ => GitFileState.Modified,
    };

    private static void ParseHeader(string record, ref string? branch, ref string? headSha, ref string? upstream,
        ref bool detached, ref bool unborn, ref int ahead, ref int behind, ref int stashCount)
    {
        var parts = record.Split(' ', 3);
        if (parts.Length < 3)
        {
            return;
        }

        var value = parts[2].TrimEnd('\n');
        switch (parts[1])
        {
            case "branch.oid":
                unborn = value == "(initial)";
                headSha = unborn ? null : value;
                break;
            case "branch.head":
                detached = value == "(detached)";
                branch = detached ? null : value;
                break;
            case "branch.upstream":
                upstream = value;
                break;
            case "branch.ab":
                foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length > 1 && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    {
                        if (token[0] == '+')
                        {
                            ahead = count;
                        }
                        else if (token[0] == '-')
                        {
                            behind = count;
                        }
                    }
                }

                break;
            case "stash":
                stashCount = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var stashes) ? stashes : 0;
                break;
        }
    }

    private static void AddIfValid(List<GitStatusEntry> entries, GitStatusEntry? entry)
    {
        if (entry is not null)
        {
            entries.Add(entry);
        }
    }

    // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
    private static GitStatusEntry? ParseOrdinary(string record)
    {
        var parts = record.Split(' ', 9);
        if (parts.Length < 9 || parts[1].Length < 2)
        {
            return null;
        }

        var xy = parts[1];
        return new GitStatusEntry
        {
            Path = parts[8],
            IndexState = MapStatusCode(xy[0]),
            WorkTreeState = MapStatusCode(xy[1]),
        };
    }

    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>, followed by <origPath> as the next record.
    private static GitStatusEntry? ParseRenamed(string record, string? originalPath)
    {
        var parts = record.Split(' ', 10);
        if (parts.Length < 10 || parts[1].Length < 2)
        {
            return null;
        }

        var xy = parts[1];
        return new GitStatusEntry
        {
            Path = parts[9],
            OriginalPath = string.IsNullOrEmpty(originalPath) ? null : originalPath,
            IndexState = MapStatusCode(xy[0]),
            WorkTreeState = MapStatusCode(xy[1]),
        };
    }

    // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>: every unmerged combination (UU, AA, DU…)
    // is a conflict the user has to resolve, so both sides are reported as Conflicted.
    private static GitStatusEntry? ParseUnmerged(string record)
    {
        var parts = record.Split(' ', 11);
        if (parts.Length < 11)
        {
            return null;
        }

        return new GitStatusEntry
        {
            Path = parts[10],
            IndexState = GitFileState.Conflicted,
            WorkTreeState = GitFileState.Conflicted,
        };
    }
}
