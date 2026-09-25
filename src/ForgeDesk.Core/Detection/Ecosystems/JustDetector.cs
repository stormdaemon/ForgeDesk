using System.Text.RegularExpressions;

namespace ForgeDesk.Core.Detection.Ecosystems;

/// <summary>just: public recipes of the root justfile that can run without arguments.</summary>
internal sealed partial class JustDetector : IEcosystemDetector
{
    public const int MaxRecipes = 30;

    public string Name => "just";

    public async Task ContributeAsync(DetectionContext context, CancellationToken cancellationToken)
    {
        var justfile = context.FirstExisting("justfile", "Justfile", ".justfile");
        if (justfile is null)
        {
            return;
        }

        context.AddBuildSystem("just");
        context.AddTechnology("just", TechnologyKind.BuildTool, justfile);
        var text = await context.ReadTextAsync(justfile, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return;
        }

        foreach (var (recipe, description) in Recipes(text).Take(MaxRecipes))
        {
            context.AddCommand(new DetectedCommand
            {
                Id = $"just:{recipe}",
                Name = recipe,
                CommandLine = $"just {recipe}",
                Category = CommandCategorizer.FromName(recipe),
                Source = justfile,
                Description = description,
            });
        }
    }

    internal static IReadOnlyList<(string Recipe, string? Description)> Recipes(string justfile)
    {
        var recipes = new List<(string, string?)>();
        string? comment = null;
        string? documented = null;
        var isPrivate = false;

        foreach (var rawLine in justfile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                comment = null;
                documented = null;
                isPrivate = false;
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                comment = line.TrimStart('#').Trim();
                continue;
            }

            if (line.StartsWith('['))
            {
                isPrivate |= line.Contains("private", StringComparison.Ordinal);
                if (DocAttribute().Match(line) is { Success: true } doc)
                {
                    documented = doc.Groups["text"].Value;
                }

                continue;
            }

            var match = RecipeLine().Match(line);
            if (match.Success && !IsKeywordLine(line))
            {
                var name = match.Groups["name"].Value;
                if (!isPrivate && !name.StartsWith('_') && !HasRequiredParameters(match.Groups["params"].Value) && !recipes.Any(r => r.Item1 == name))
                {
                    recipes.Add((name, documented ?? comment));
                }
            }

            comment = null;
            documented = null;
            isPrivate = false;
        }

        return recipes;
    }

    private static bool IsKeywordLine(string line) =>
        line.StartsWith("set ", StringComparison.Ordinal) || line.StartsWith("alias ", StringComparison.Ordinal)
        || line.StartsWith("export ", StringComparison.Ordinal) || line.StartsWith("import ", StringComparison.Ordinal)
        || line.StartsWith("mod ", StringComparison.Ordinal);

    /// <summary>"deploy env:" needs an argument; "build mode='debug':" and "test *args:" do not.</summary>
    private static bool HasRequiredParameters(string parameters) =>
        parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => !p.Contains('=', StringComparison.Ordinal) && !p.StartsWith('*') && p[0] is not '\'' and not '"');

    // "name:", "@name:", "name arg='x':" — but not "name := value".
    [GeneratedRegex(@"^@?(?<name>[A-Za-z_][A-Za-z0-9_-]*)(?<params>[^:=]*(?:=[^:]*)?)?:(?!=)", RegexOptions.CultureInvariant)]
    private static partial Regex RecipeLine();

    [GeneratedRegex(@"doc\(\s*['""](?<text>[^'""]*)['""]\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex DocAttribute();
}
