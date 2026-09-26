namespace ForgeDesk.Core.Analysis.Dependencies;

/// <summary>
/// Cargo.toml: <c>[dependencies]</c>, <c>[dev-dependencies]</c>, <c>[build-dependencies]</c>, their
/// <c>[target.'cfg(…)'.*]</c> variants and <c>[dependencies.name]</c> sub-tables. Members declared
/// with <c>workspace = true</c> get their version from the root's <c>[workspace.dependencies]</c>.
/// </summary>
internal static class CargoDependencyParser
{
    public const string Ecosystem = "Cargo";

    private static readonly HashSet<string> DependencyTables = new(StringComparer.Ordinal)
    {
        "dependencies", "dev-dependencies", "build-dependencies",
    };

    /// <summary>Versions declared in <c>[workspace.dependencies]</c>, by crate name.</summary>
    public static IReadOnlyDictionary<string, string> ReadWorkspaceVersions(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dependency in Collect(content, workspaceOnly: true))
        {
            if (dependency.Version is not null)
            {
                versions[dependency.Key] = dependency.Version;
            }
        }

        return versions;
    }

    public static IReadOnlyList<DependencyInfo> Parse(string content, string manifestPath, IReadOnlyDictionary<string, string>? workspaceVersions = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Collect(content, workspaceOnly: false)
            .Select(d => new DependencyInfo(
                Ecosystem,
                d.Name,
                d.InheritsWorkspace ? (workspaceVersions?.GetValueOrDefault(d.Key) ?? d.Version ?? "workspace") : d.Version,
                d.IsDevelopment,
                manifestPath))
            .ToList();
    }

    private static IEnumerable<CrateSpec> Collect(string content, bool workspaceOnly)
    {
        var crates = new Dictionary<(string Table, string Key), CrateSpec>();
        var order = new List<(string, string)>();

        CrateSpec For(IReadOnlyList<string> table, string key)
        {
            var id = (string.Join('.', table), key);
            if (!crates.TryGetValue(id, out var spec))
            {
                var kind = table[^1];
                spec = new CrateSpec(key) { IsDevelopment = kind != "dependencies" };
                crates[id] = spec;
                order.Add(id);
            }

            return spec;
        }

        foreach (var entry in TomlLite.Parse(content))
        {
            var table = entry.Table;
            var isWorkspace = table.Count > 0 && table[0] == "workspace";
            if (isWorkspace != workspaceOnly)
            {
                continue;
            }

            if (table.Count > 0 && DependencyTables.Contains(table[^1]))
            {
                // serde = "1" · serde = { version = "1" } · serde.workspace = true
                var spec = For(table, entry.Key[0]);
                if (entry.Key.Count == 1)
                {
                    spec.Apply(entry.Value);
                }
                else
                {
                    spec.ApplyField(string.Join('.', entry.Key.Skip(1)), entry.Value);
                }
            }
            else if (table.Count > 1 && DependencyTables.Contains(table[^2]))
            {
                // [dependencies.serde] followed by version = "1"
                For(table.Take(table.Count - 1).ToList(), table[^1]).ApplyField(string.Join('.', entry.Key), entry.Value);
            }
        }

        return order.Select(id => crates[id]);
    }

    private sealed class CrateSpec(string key)
    {
        public string Key { get; } = key;
        public string? Version { get; private set; }
        public string? Package { get; private set; }
        public bool InheritsWorkspace { get; private set; }
        public bool IsDevelopment { get; init; }

        /// <summary>The crate name, which "package = …" can rename.</summary>
        public string Name => Package ?? Key;

        public void Apply(string value)
        {
            if (TomlLite.TryGetString(value, out var version))
            {
                Version = version;
                return;
            }

            foreach (var (field, raw) in TomlLite.GetInlineTable(value))
            {
                ApplyField(field, raw);
            }
        }

        public void ApplyField(string field, string value)
        {
            switch (field)
            {
                case "version" when TomlLite.TryGetString(value, out var version):
                    Version = version;
                    break;
                case "package" when TomlLite.TryGetString(value, out var package):
                    Package = package;
                    break;
                case "workspace" when value.Trim() == "true":
                    InheritsWorkspace = true;
                    break;
            }
        }
    }
}
