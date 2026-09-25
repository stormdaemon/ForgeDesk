namespace ForgeDesk.Core.Analysis;

/// <summary>How comments start in a language (combinable).</summary>
[Flags]
internal enum CommentSyntax
{
    None = 0,

    /// <summary><c>// …</c></summary>
    DoubleSlash = 1,

    /// <summary><c>/* … */</c>, including the <c> * </c> continuation lines of block comments.</summary>
    SlashStar = 2,

    /// <summary><c># …</c></summary>
    Hash = 4,

    /// <summary><c>-- …</c></summary>
    DoubleDash = 8,

    /// <summary><c>&lt;!-- … --&gt;</c> and Razor's <c>@* … *@</c>.</summary>
    Markup = 16,

    /// <summary><c>; …</c></summary>
    Semicolon = 32,

    /// <summary><c>% …</c></summary>
    Percent = 64,

    /// <summary><c>' …</c> (Visual Basic).</summary>
    Apostrophe = 128,

    /// <summary><c>REM …</c> and <c>:: …</c> (batch files).</summary>
    Rem = 256,

    CLike = DoubleSlash | SlashStar,
}

/// <summary>A language counted in the health report, with its GitHub Linguist color.</summary>
internal sealed record LanguageDefinition(string Name, string Color, CommentSyntax Comments);

/// <summary>
/// Small, curated subset of GitHub Linguist: programming and markup languages developers expect
/// to see in a language bar. Data and prose formats (JSON, YAML, Markdown…) are deliberately absent.
/// </summary>
internal static class LanguageCatalog
{
    private static readonly Dictionary<string, LanguageDefinition> ByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LanguageDefinition> ByFileName = new(StringComparer.OrdinalIgnoreCase);

    static LanguageCatalog()
    {
        Add("C#", "#178600", CommentSyntax.CLike, ".cs", ".csx");
        Add("F#", "#b845fc", CommentSyntax.DoubleSlash, ".fs", ".fsi", ".fsx");
        Add("Visual Basic .NET", "#945db7", CommentSyntax.Apostrophe, ".vb");
        Add("TypeScript", "#3178c6", CommentSyntax.CLike, ".ts", ".tsx", ".mts", ".cts");
        Add("JavaScript", "#f1e05a", CommentSyntax.CLike, ".js", ".jsx", ".mjs", ".cjs");
        Add("Python", "#3572A5", CommentSyntax.Hash, ".py", ".pyw", ".pyi");
        Add("Rust", "#dea584", CommentSyntax.CLike, ".rs");
        Add("Go", "#00ADD8", CommentSyntax.CLike, ".go");
        Add("Java", "#b07219", CommentSyntax.CLike, ".java");
        Add("Kotlin", "#A97BFF", CommentSyntax.CLike, ".kt", ".kts");
        Add("Scala", "#c22d40", CommentSyntax.CLike, ".scala", ".sc");
        Add("Groovy", "#4298b8", CommentSyntax.CLike, ".groovy", ".gradle");
        Add("C", "#555555", CommentSyntax.CLike, ".c", ".h");
        Add("C++", "#f34b7d", CommentSyntax.CLike, ".cpp", ".cc", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".ino");
        Add("Objective-C", "#438eff", CommentSyntax.CLike, ".m", ".mm");
        Add("Swift", "#F05138", CommentSyntax.CLike, ".swift");
        Add("Dart", "#00B4AB", CommentSyntax.CLike, ".dart");
        Add("Ruby", "#701516", CommentSyntax.Hash, ".rb", ".rake", ".gemspec");
        Add("PHP", "#4F5D95", CommentSyntax.CLike | CommentSyntax.Hash, ".php");
        Add("Perl", "#0298c3", CommentSyntax.Hash, ".pl", ".pm");
        Add("Lua", "#000080", CommentSyntax.DoubleDash, ".lua");
        Add("R", "#198CE7", CommentSyntax.Hash, ".r");
        Add("Julia", "#a270ba", CommentSyntax.Hash, ".jl");
        Add("Elixir", "#6e4a7e", CommentSyntax.Hash, ".ex", ".exs");
        Add("Erlang", "#B83998", CommentSyntax.Percent, ".erl", ".hrl");
        Add("Haskell", "#5e5086", CommentSyntax.DoubleDash, ".hs");
        Add("Elm", "#60B5CC", CommentSyntax.DoubleDash, ".elm");
        Add("OCaml", "#ef7a08", CommentSyntax.None, ".ml", ".mli");
        Add("Clojure", "#db5855", CommentSyntax.Semicolon, ".clj", ".cljs", ".cljc");
        Add("Zig", "#ec915c", CommentSyntax.DoubleSlash, ".zig");
        Add("Nim", "#ffc200", CommentSyntax.Hash, ".nim");
        Add("Crystal", "#000100", CommentSyntax.Hash, ".cr");
        Add("Pascal", "#E3F171", CommentSyntax.DoubleSlash, ".pas");
        Add("Assembly", "#6E4C13", CommentSyntax.Semicolon, ".asm");
        Add("Solidity", "#AA6746", CommentSyntax.CLike, ".sol");
        Add("GDScript", "#355570", CommentSyntax.Hash, ".gd");
        Add("Shell", "#89e051", CommentSyntax.Hash, ".sh", ".bash", ".zsh", ".fish");
        Add("PowerShell", "#012456", CommentSyntax.Hash, ".ps1", ".psm1", ".psd1");
        Add("Batchfile", "#C1F12E", CommentSyntax.Rem, ".bat", ".cmd");
        Add("SQL", "#e38c00", CommentSyntax.DoubleDash | CommentSyntax.SlashStar, ".sql");
        Add("HCL", "#844FBA", CommentSyntax.CLike | CommentSyntax.Hash, ".tf", ".tfvars", ".hcl");
        Add("Nix", "#7e7eff", CommentSyntax.Hash, ".nix");
        Add("Starlark", "#76d275", CommentSyntax.Hash, ".bzl");
        Add("CMake", "#DA3434", CommentSyntax.Hash, ".cmake");
        Add("Makefile", "#427819", CommentSyntax.Hash, ".mk");
        Add("Dockerfile", "#384d54", CommentSyntax.Hash, ".dockerfile");
        Add("Vue", "#41b883", CommentSyntax.CLike | CommentSyntax.Markup, ".vue");
        Add("Svelte", "#ff3e00", CommentSyntax.CLike | CommentSyntax.Markup, ".svelte");
        Add("Astro", "#ff5a03", CommentSyntax.CLike | CommentSyntax.Markup, ".astro");
        Add("HTML", "#e34c26", CommentSyntax.Markup, ".html", ".htm");
        Add("CSS", "#563d7c", CommentSyntax.SlashStar, ".css");
        Add("SCSS", "#c6538c", CommentSyntax.CLike, ".scss");
        Add("Sass", "#a53b70", CommentSyntax.DoubleSlash, ".sass");
        Add("Less", "#1d365d", CommentSyntax.CLike, ".less");
        Add("XAML", "#0C54C2", CommentSyntax.Markup, ".xaml", ".axaml");
        Add("Razor", "#512be4", CommentSyntax.CLike | CommentSyntax.Markup, ".cshtml", ".razor");

        Name("Dockerfile", "Dockerfile", "Containerfile");
        Name("Makefile", "Makefile", "GNUmakefile");
        Name("CMake", "CMakeLists.txt");
        Name("Ruby", "Rakefile", "Gemfile", "Vagrantfile", "Podfile", "Fastfile", "Brewfile");
        Name("Groovy", "Jenkinsfile");
        Name("Starlark", "BUILD", "BUILD.bazel", "WORKSPACE", "WORKSPACE.bazel", "MODULE.bazel");
    }

    /// <summary>The language of a file (relative path or name), or null when it is not counted.</summary>
    public static LanguageDefinition? Detect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slash = path.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        if (ByFileName.TryGetValue(name, out var byName))
        {
            return byName;
        }

        if (name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase))
        {
            return ByFileName["Dockerfile"];
        }

        var dot = name.LastIndexOf('.');
        return dot > 0 && ByExtension.TryGetValue(name[dot..], out var byExtension) ? byExtension : null;
    }

    private static void Add(string name, string color, CommentSyntax comments, params string[] extensions)
    {
        var language = new LanguageDefinition(name, color, comments);
        foreach (var extension in extensions)
        {
            ByExtension.Add(extension, language);
        }
    }

    private static void Name(string language, params string[] fileNames)
    {
        var definition = ByExtension.Values.First(l => l.Name == language);
        foreach (var fileName in fileNames)
        {
            ByFileName.Add(fileName, definition);
        }
    }
}
