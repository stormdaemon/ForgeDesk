using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>Make: targets of the root Makefile, excluding special, pattern, variable and file targets.</summary>
internal sealed partial class MakeDetector : IEcosystemDetector
{
    public const int MaxTargets = 30;

    public string Name => "Make";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var makefile = context.FirstExisting("GNUmakefile", "Makefile", "makefile");
        if (makefile is null)
        {
            return;
        }

        context.AddBuildSystem("Make");
        context.AddTechnology("Make", TechnologyKind.BuildTool, makefile);
        var text = await context.ReadTextAsync(makefile, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return;
        }

        foreach (var (target, description) in Targets(text).Take(MaxTargets))
        {
            context.AddCommand(new DetectedCommand
            {
                Id = $"make:{target}",
                Name = target,
                CommandLine = $"make {target}",
                Category = CommandCategorizer.FromName(target),
                Source = makefile,
                Description = description,
            });
        }
    }

    /// <summary>Rule targets in file order, with the "## help" text or the comment line above them.</summary>
    internal static IReadOnlyList<(string Target, string? Description)> Targets(string makefile)
    {
        var targets = new List<(string, string?)>();
        string? previousComment = null;
        var inDefine = false;

        foreach (var rawLine in makefile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmedStart = line.TrimStart();
            if (inDefine)
            {
                inDefine = !trimmedStart.StartsWith("endef", StringComparison.Ordinal);
                continue;
            }

            if (trimmedStart.StartsWith("define ", StringComparison.Ordinal) || trimmedStart == "define")
            {
                inDefine = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                previousComment = line.TrimStart('#').Trim();
                continue;
            }

            // Recipe lines start with a tab; blank lines break the link between a comment and a rule.
            if (line.Length == 0 || line.StartsWith('\t') || line.StartsWith(' '))
            {
                previousComment = line.Length == 0 ? null : previousComment;
                continue;
            }

            var match = RuleLine().Match(line);
            if (match.Success)
            {
                var rest = match.Groups["rest"].Value;
                var help = rest.Contains("##", StringComparison.Ordinal) ? rest[(rest.IndexOf("##", StringComparison.Ordinal) + 2)..].Trim() : null;
                var description = string.IsNullOrEmpty(help) ? previousComment : help;
                foreach (var name in match.Groups["names"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (IsUserFacing(name) && !targets.Any(t => t.Item1 == name))
                    {
                        targets.Add((name, string.IsNullOrEmpty(description) ? null : description));
                    }
                }
            }

            previousComment = null;
        }

        return targets;
    }

    private static bool IsUserFacing(string name) =>
        !name.StartsWith('_')
        && !name.Equals("FORCE", StringComparison.Ordinal)
        && name.IndexOfAny(['/', '.', '$', '%', '\\']) < 0;

    // "name:", "a b:", "name::" — but not "VAR := x", "VAR ::= x" or ".PHONY:".
    [GeneratedRegex(@"^(?<names>[A-Za-z0-9_][A-Za-z0-9_.\-/ ]*?)\s*::?(?!=)(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleLine();
}
