using System.IO;

namespace ForgeDesk.App.Controls.Code;

/// <summary>
/// Chooses an AvalonEdit highlighting definition name from a file name. Covers AvalonEdit's
/// built-in languages plus the common extensions they handle well enough (TypeScript through the
/// JavaScript rules, MSBuild files through XML…). Returns null for plain text.
/// </summary>
internal static class SyntaxLanguageMap
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // C# and .NET
        [".cs"] = "C#",
        [".csx"] = "C#",
        [".cake"] = "C#",
        [".vb"] = "VB",
        [".vbs"] = "VB",

        // Web
        [".js"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".ts"] = "JavaScript",
        [".mts"] = "JavaScript",
        [".cts"] = "JavaScript",
        [".tsx"] = "JavaScript",
        [".json"] = "Json",
        [".jsonc"] = "Json",
        [".json5"] = "Json",
        [".webmanifest"] = "Json",
        [".htm"] = "HTML",
        [".html"] = "HTML",
        [".xhtml"] = "HTML",
        [".vue"] = "HTML",
        [".svelte"] = "HTML",
        [".razor"] = "HTML",
        [".cshtml"] = "HTML",
        [".asp"] = "ASP/XHTML",
        [".aspx"] = "ASP/XHTML",
        [".ascx"] = "ASP/XHTML",
        [".master"] = "ASP/XHTML",
        [".css"] = "CSS",
        [".scss"] = "CSS",
        [".less"] = "CSS",
        [".php"] = "PHP",

        // XML family (MSBuild, XAML, manifests…)
        [".xml"] = "XML",
        [".xaml"] = "XML",
        [".axaml"] = "XML",
        [".csproj"] = "XML",
        [".fsproj"] = "XML",
        [".vbproj"] = "XML",
        [".vcxproj"] = "XML",
        [".proj"] = "XML",
        [".props"] = "XML",
        [".targets"] = "XML",
        [".slnx"] = "XML",
        [".nuspec"] = "XML",
        [".resx"] = "XML",
        [".config"] = "XML",
        [".manifest"] = "XML",
        [".xsd"] = "XML",
        [".xsl"] = "XML",
        [".xslt"] = "XML",
        [".svg"] = "XML",
        [".plist"] = "XML",
        [".xlf"] = "XML",
        [".runsettings"] = "XML",
        [".wxs"] = "XML",

        // Native and JVM
        [".c"] = "C++",
        [".h"] = "C++",
        [".cc"] = "C++",
        [".cpp"] = "C++",
        [".cxx"] = "C++",
        [".hh"] = "C++",
        [".hpp"] = "C++",
        [".hxx"] = "C++",
        [".ino"] = "C++",
        [".java"] = "Java",
        [".kt"] = "Java",
        [".kts"] = "Java",
        [".groovy"] = "Java",
        [".gradle"] = "Java",

        // Scripting and data
        [".py"] = "Python",
        [".pyw"] = "Python",
        [".pyi"] = "Python",
        [".ps1"] = "PowerShell",
        [".psm1"] = "PowerShell",
        [".psd1"] = "PowerShell",
        [".sql"] = "TSQL",
        [".md"] = "MarkDown",
        [".markdown"] = "MarkDown",
        [".mdx"] = "MarkDown",
        [".tex"] = "TeX",
        [".patch"] = "Patch",
        [".diff"] = "Patch",
        [".boo"] = "Boo",
    };

    public static string? DefinitionNameFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extension = Path.GetExtension(path.Trim());
        return extension.Length > 0 && ByExtension.TryGetValue(extension, out var name) ? name : null;
    }
}
