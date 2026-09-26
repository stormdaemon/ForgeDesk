using System.Collections.Frozen;

namespace ForgeDesk.Core.Detection;

/// <summary>How a language counts in statistics, mirroring GitHub Linguist's language types.</summary>
internal enum LanguageType
{
    Programming,
    Markup,
    Data,
    Prose,
}

internal sealed record LanguageDefinition(string Name, string Color, LanguageType Type);

/// <summary>File extension / file name → language, with GitHub Linguist colors.</summary>
internal static class LanguageCatalog
{
    public const string OtherColor = "#8B949E";

    private static readonly (FrozenDictionary<string, LanguageDefinition> Extensions, FrozenDictionary<string, LanguageDefinition> FileNames) Maps = Build();

    private static FrozenDictionary<string, LanguageDefinition> ByExtension => Maps.Extensions;

    private static FrozenDictionary<string, LanguageDefinition> ByFileName => Maps.FileNames;

    private static (FrozenDictionary<string, LanguageDefinition>, FrozenDictionary<string, LanguageDefinition>) Build()
    {
        var extensions = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string color, LanguageType type, params string[] keys)
        {
            var definition = new LanguageDefinition(name, color, type);
            foreach (var key in keys)
            {
                if (key.StartsWith('.'))
                {
                    extensions[key] = definition;
                }
                else
                {
                    fileNames[key] = definition;
                }
            }
        }

        const LanguageType P = LanguageType.Programming;
        const LanguageType M = LanguageType.Markup;
        const LanguageType D = LanguageType.Data;
        const LanguageType T = LanguageType.Prose;

        Add("C#", "#178600", P, ".cs", ".csx");
        Add("F#", "#B845FC", P, ".fs", ".fsi", ".fsx");
        Add("Visual Basic .NET", "#945DB7", P, ".vb");
        Add("JavaScript", "#F1E05A", P, ".js", ".mjs", ".cjs", ".jsx");
        Add("TypeScript", "#3178C6", P, ".ts", ".mts", ".cts", ".tsx");
        Add("Python", "#3572A5", P, ".py", ".pyw", ".pyi");
        Add("Jupyter Notebook", "#DA5B0B", M, ".ipynb");
        Add("Java", "#B07219", P, ".java");
        Add("Kotlin", "#A97BFF", P, ".kt", ".kts");
        Add("Scala", "#C22D40", P, ".scala", ".sc");
        Add("Groovy", "#4298B8", P, ".groovy", ".gradle", "Jenkinsfile");
        Add("Go", "#00ADD8", P, ".go");
        Add("Rust", "#DEA584", P, ".rs");
        Add("C", "#555555", P, ".c", ".h");
        Add("C++", "#F34B7D", P, ".cpp", ".cc", ".cxx", ".c++", ".hpp", ".hh", ".hxx", ".h++", ".ino", ".ipp", ".tpp");
        Add("Objective-C", "#438EFF", P, ".m");
        Add("Objective-C++", "#6866FB", P, ".mm");
        Add("Swift", "#F05138", P, ".swift");
        Add("Ruby", "#701516", P, ".rb", ".rake", ".gemspec", ".ru", "Rakefile", "Gemfile", "Podfile", "Vagrantfile", "Guardfile", "Fastfile");
        Add("PHP", "#4F5D95", P, ".php", ".phtml");
        Add("Blade", "#F7523F", M, ".blade.php");
        Add("HTML", "#E34C26", M, ".html", ".htm", ".xhtml");
        Add("CSS", "#663399", M, ".css");
        Add("SCSS", "#C6538C", M, ".scss");
        Add("Sass", "#A53B70", M, ".sass");
        Add("Less", "#1D365D", M, ".less");
        Add("Stylus", "#FF6347", M, ".styl");
        Add("Vue", "#41B883", M, ".vue");
        Add("Svelte", "#FF3E00", M, ".svelte");
        Add("Astro", "#FF5A03", M, ".astro");
        Add("MDX", "#FCB32C", M, ".mdx");
        Add("Razor", "#512BE4", M, ".cshtml", ".razor");
        Add("XAML", "#0060AC", M, ".xaml", ".axaml");
        Add("Handlebars", "#F7931E", M, ".hbs", ".handlebars");
        Add("Pug", "#A86454", M, ".pug");
        Add("EJS", "#A91E50", M, ".ejs");
        Add("Dart", "#00B4AB", P, ".dart");
        Add("Shell", "#89E051", P, ".sh", ".bash", ".zsh", ".ksh", ".fish");
        Add("PowerShell", "#012456", P, ".ps1", ".psm1", ".psd1");
        Add("Batchfile", "#C1F12E", P, ".bat", ".cmd");
        Add("Lua", "#000080", P, ".lua");
        Add("Perl", "#0298C3", P, ".pl", ".pm");
        Add("R", "#198CE7", P, ".r");
        Add("Julia", "#A270BA", P, ".jl");
        Add("Elixir", "#6E4A7E", P, ".ex", ".exs");
        Add("Erlang", "#B83998", P, ".erl", ".hrl");
        Add("Haskell", "#5E5086", P, ".hs", ".lhs");
        Add("OCaml", "#EF7A08", P, ".ml", ".mli");
        Add("Clojure", "#DB5855", P, ".clj", ".cljs", ".cljc", ".edn");
        Add("Elm", "#60B5CC", P, ".elm");
        Add("PureScript", "#1D222D", P, ".purs");
        Add("ReScript", "#ED5051", P, ".res", ".resi");
        Add("Gleam", "#FFAFF3", P, ".gleam");
        Add("Zig", "#EC915C", P, ".zig");
        Add("Nim", "#FFC200", P, ".nim");
        Add("Crystal", "#000100", P, ".cr");
        Add("D", "#BA595E", P, ".d");
        Add("Fortran", "#4D41B1", P, ".f90", ".f95", ".f03", ".f08", ".for");
        Add("Pascal", "#E3F171", P, ".pas", ".dpr", ".lpr");
        Add("Assembly", "#6E4C13", P, ".asm", ".s", ".nasm");
        Add("CoffeeScript", "#244776", P, ".coffee");
        Add("Haxe", "#DF7900", P, ".hx");
        Add("Solidity", "#AA6746", P, ".sol");
        Add("Cuda", "#3A4E3A", P, ".cu", ".cuh");
        Add("GLSL", "#5686A5", P, ".glsl", ".vert", ".frag", ".comp");
        Add("HLSL", "#AACE60", P, ".hlsl", ".fx");
        Add("VHDL", "#ADB2CB", P, ".vhd", ".vhdl");
        Add("Verilog", "#B2B7F8", P, ".v");
        Add("SystemVerilog", "#DAE1C2", P, ".sv", ".svh");
        Add("Vim Script", "#199F4B", P, ".vim");
        Add("Emacs Lisp", "#C065DB", P, ".el");
        Add("Common Lisp", "#3FB68B", P, ".lisp", ".lsp");
        Add("Scheme", "#1E4AEC", P, ".scm", ".ss");
        Add("Racket", "#3C5CAA", P, ".rkt");
        Add("AutoHotkey", "#6594B9", P, ".ahk");
        Add("Inno Setup", "#264B99", P, ".iss");
        Add("Mojo", "#FF4C1F", P, ".mojo");
        Add("Odin", "#60AFFE", P, ".odin");
        Add("Starlark", "#76D275", P, ".bzl", ".star", "BUILD.bazel", "WORKSPACE", "WORKSPACE.bazel");
        Add("WebAssembly", "#04133B", P, ".wat", ".wast");
        Add("Makefile", "#427819", P, ".mk", ".mak", "Makefile", "makefile", "GNUmakefile");
        Add("CMake", "#DA3434", P, ".cmake", "CMakeLists.txt");
        Add("Dockerfile", "#384D54", P, ".dockerfile", "Dockerfile", "Containerfile");
        Add("Just", "#384D54", P, ".just", "justfile", ".justfile");
        Add("HCL", "#844FBA", P, ".tf", ".tfvars", ".hcl");
        Add("Nix", "#7E7EFF", P, ".nix");
        Add("SQL", "#E38C00", D, ".sql");
        Add("PLpgSQL", "#336790", P, ".pgsql", ".plpgsql");
        Add("GraphQL", "#E10098", D, ".graphql", ".gql");
        Add("Protocol Buffer", "#6A737D", D, ".proto");
        Add("Jsonnet", "#0064BD", P, ".jsonnet", ".libsonnet");
        Add("TeX", "#3D6117", M, ".tex", ".sty");
        Add("Markdown", "#083FA1", T, ".md", ".markdown");
        Add("reStructuredText", "#141414", T, ".rst");
        Add("AsciiDoc", "#73A0C5", T, ".adoc", ".asciidoc");
        Add("JSON", "#292929", D, ".json", ".jsonc", ".json5");
        Add("YAML", "#CB171E", D, ".yml", ".yaml");
        Add("TOML", "#9C4221", D, ".toml");
        Add("XML", "#0060AC", D, ".xml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".resx", ".nuspec", ".plist", ".xsd", ".xsl");
        Add("INI", "#D1DBE0", D, ".ini", ".cfg", ".editorconfig");
        Add("CSV", "#237346", D, ".csv", ".tsv");

        return (extensions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), fileNames.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Number of languages the catalog knows.</summary>
    public static int Count => ByExtension.Values.Concat(ByFileName.Values).Select(d => d.Name).Distinct(StringComparer.Ordinal).Count();

    public static LanguageDefinition? Find(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var fileName = RelativePaths.FileName(relativePath);
        if (ByFileName.TryGetValue(fileName, out var byName))
        {
            return byName;
        }

        // Double extensions first (".blade.php"), then the plain extension.
        var firstDot = fileName.IndexOf('.', 1);
        if (firstDot > 0)
        {
            var lastDot = fileName.LastIndexOf('.');
            if (lastDot > firstDot)
            {
                var secondToLast = fileName.LastIndexOf('.', lastDot - 1);
                if (secondToLast > 0 && ByExtension.TryGetValue(fileName[secondToLast..], out var compound))
                {
                    return compound;
                }
            }
        }

        if (fileName.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase))
        {
            return ByFileName["Dockerfile"];
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && ByExtension.TryGetValue(extension, out var byExtension) ? byExtension : null;
    }

    public static string ColorOf(string language)
    {
        var match = ByExtension.Values.Concat(ByFileName.Values).FirstOrDefault(d => d.Name.Equals(language, StringComparison.OrdinalIgnoreCase));
        return match?.Color ?? OtherColor;
    }
}
