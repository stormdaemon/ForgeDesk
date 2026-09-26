using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>
/// Gemfile: <c>gem "name", "~> 1.0"</c> lines. Gems inside <c>group :development, :test do … end</c>
/// blocks, or declared with a <c>group:</c> option naming only those groups, are development gems.
/// </summary>
internal static partial class GemfileDependencyParser
{
    public const string Ecosystem = "RubyGems";

    private static readonly HashSet<string> DevelopmentGroups = new(StringComparer.OrdinalIgnoreCase) { "development", "test" };

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new List<DependencyInfo>();

        // One entry per open "do … end" block: is it a development-only group?
        var blocks = new Stack<bool>();
        foreach (var raw in content.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var inDevelopmentBlock = blocks.Count > 0 && blocks.Peek();
            var group = GroupBlock().Match(line);
            if (group.Success)
            {
                blocks.Push(inDevelopmentBlock || IsDevelopmentOnly(group.Groups["groups"].Value));
                continue;
            }

            if (BlockEnd().IsMatch(line))
            {
                if (blocks.Count > 0)
                {
                    blocks.Pop();
                }

                continue;
            }

            if (BlockStart().IsMatch(line))
            {
                // platforms, source, git, path… blocks inherit the enclosing group.
                blocks.Push(inDevelopmentBlock);
            }

            var gem = Gem().Match(line);
            if (!gem.Success)
            {
                continue;
            }

            var arguments = gem.Groups["rest"].Value;
            var versions = Strings().Matches(arguments)
                .Select(m => m.Groups["value"].Value)
                .TakeWhile(IsVersionConstraint)
                .ToList();
            var optionGroups = GroupOption().Match(arguments);
            var isDevelopment = inDevelopmentBlock || (optionGroups.Success && IsDevelopmentOnly(optionGroups.Groups["groups"].Value));
            result.Add(new DependencyInfo(Ecosystem, gem.Groups["name"].Value, versions.Count == 0 ? null : string.Join(", ", versions), isDevelopment, manifestPath));
        }

        return result;
    }

    private static bool IsDevelopmentOnly(string groups)
    {
        var names = Symbol().Matches(groups).Select(m => m.Groups["name"].Value).ToList();
        return names.Count > 0 && names.All(DevelopmentGroups.Contains);
    }

    // Constraints only appear right after the name, before options like require: false.
    private static bool IsVersionConstraint(string value) => VersionConstraint().IsMatch(value);

    private static string StripComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    [GeneratedRegex(@"^gem\s*\(?\s*(['""])(?<name>[^'""]+)\1(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Gem();

    [GeneratedRegex(@"^group\s*\(?(?<groups>.+?)\)?\s+do(\s*\|.*\|)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupBlock();

    [GeneratedRegex(@"\bdo(\s*\|[^|]*\|)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^end\b", RegexOptions.CultureInvariant)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"(['""])(?<value>[^'""]*)\1", RegexOptions.CultureInvariant)]
    private static partial Regex Strings();

    [GeneratedRegex(@"(?:group|groups)\s*(?::|=>)\s*(?<groups>\[[^\]]*\]|:[A-Za-z_]+|['""][^'""]+['""])", RegexOptions.CultureInvariant)]
    private static partial Regex GroupOption();

    [GeneratedRegex(@":?['""]?(?<name>[A-Za-z_]+)['""]?", RegexOptions.CultureInvariant)]
    private static partial Regex Symbol();

    [GeneratedRegex(@"^\s*(?:~>|>=|<=|!=|=|>|<)?\s*[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex VersionConstraint();
}
