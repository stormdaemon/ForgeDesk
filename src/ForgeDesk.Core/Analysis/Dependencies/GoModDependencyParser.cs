namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>
/// go.mod: <c>require</c> lines and <c>require ( … )</c> blocks. Indirect requirements (marked
/// <c>// indirect</c>) are transitive dependencies Go records for reproducibility; they are skipped.
/// </summary>
internal static class GoModDependencyParser
{
    public const string Ecosystem = "Go";

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = new List<DependencyInfo>();
        var inRequireBlock = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (inRequireBlock)
            {
                if (line.StartsWith(')'))
                {
                    inRequireBlock = false;
                }
                else
                {
                    Add(line);
                }

                continue;
            }

            if (!line.StartsWith("require", StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line["require".Length..].Trim();
            if (rest.StartsWith('('))
            {
                inRequireBlock = !rest.Contains(')', StringComparison.Ordinal);
                continue;
            }

            if (line.Length > "require".Length && char.IsWhiteSpace(line["require".Length]))
            {
                Add(rest);
            }
        }

        return result;

        void Add(string requirement)
        {
            var comment = requirement.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                if (requirement[comment..].Contains("indirect", StringComparison.Ordinal))
                {
                    return;
                }

                requirement = requirement[..comment];
            }

            var parts = requirement.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                result.Add(new DependencyInfo(Ecosystem, parts[0].Trim('"'), parts.Length > 1 ? parts[1] : null, false, manifestPath));
            }
        }
    }
}
