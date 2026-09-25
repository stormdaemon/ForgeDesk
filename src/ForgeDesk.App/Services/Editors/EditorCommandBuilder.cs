using System.Globalization;
using ForgeDesk.App.Services.Launching;

namespace ForgeDesk.App.Services.Editors;

/// <summary>Turns "open this file (at this line)" into the command line each editor understands.</summary>
internal static class EditorCommandBuilder
{
    private static readonly string[] PathPlaceholders = ["{file}", "{path}"];
    private static readonly string[] PositionPlaceholders = ["{line}", "{column}"];

    public static LaunchCommand OpenFile(EditorInfo editor, string path, int? line, string commandProcessor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var lineNumber = line is > 0 ? line.Value : (int?)null;
        IEnumerable<string> arguments = editor.Family switch
        {
            EditorFamily.VisualStudioCode when lineNumber is { } l => ["-g", $"{path}:{l.ToString(CultureInfo.InvariantCulture)}"],
            EditorFamily.NotepadPlusPlus when lineNumber is { } l => [$"-n{l.ToString(CultureInfo.InvariantCulture)}", path],
            EditorFamily.Custom => ExpandTemplate(editor.ArgumentTemplate ?? string.Empty, path, lineNumber, forFolder: false),
            _ => [path],
        };

        return Create(editor.Executable, arguments, commandProcessor);
    }

    /// <summary>Returns null when the editor cannot open folders (Notepad, Notepad++).</summary>
    public static LaunchCommand? OpenFolder(EditorInfo editor, string folder, string commandProcessor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        IEnumerable<string>? arguments = editor.Family switch
        {
            EditorFamily.VisualStudioCode => [folder],
            EditorFamily.Custom => ExpandTemplate(editor.ArgumentTemplate ?? string.Empty, folder, null, forFolder: true),
            _ => null,
        };

        return arguments is null ? null : Create(editor.Executable, arguments, commandProcessor);
    }

    /// <summary>
    /// Expands a user argument template. Each token is split like Windows does, placeholders are
    /// replaced with raw values and the token is re-quoted, so users never have to think about
    /// quoting paths with spaces. Without a {file} placeholder, the path is appended.
    /// </summary>
    public static IReadOnlyList<string> ExpandTemplate(string template, string path, int? line, bool forFolder)
    {
        var result = new List<string>();
        var sawPath = false;
        var lineText = (line ?? 1).ToString(CultureInfo.InvariantCulture);
        foreach (var token in WindowsCommandLine.Split(template))
        {
            var hasPath = PathPlaceholders.Any(p => token.Contains(p, StringComparison.OrdinalIgnoreCase));
            var hasPosition = PositionPlaceholders.Any(p => token.Contains(p, StringComparison.OrdinalIgnoreCase));
            var value = token;
            if (forFolder && hasPosition)
            {
                if (!hasPath)
                {
                    // "-n{line}" and friends make no sense for a folder.
                    continue;
                }

                foreach (var placeholder in PositionPlaceholders)
                {
                    value = value.Replace(":" + placeholder, string.Empty, StringComparison.OrdinalIgnoreCase)
                        .Replace(placeholder, string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }

            foreach (var placeholder in PathPlaceholders)
            {
                value = value.Replace(placeholder, path, StringComparison.OrdinalIgnoreCase);
            }

            value = value.Replace("{line}", lineText, StringComparison.OrdinalIgnoreCase)
                .Replace("{column}", "1", StringComparison.OrdinalIgnoreCase);
            sawPath |= hasPath;
            result.Add(value);
        }

        if (!sawPath)
        {
            result.Add(path);
        }

        return result;
    }

    private static LaunchCommand Create(string executable, IEnumerable<string> arguments, string commandProcessor)
    {
        var joined = WindowsCommandLine.Join(arguments);
        return LaunchCommand.IsScript(executable)
            ? LaunchCommand.ForScript(executable, joined, commandProcessor)
            : new LaunchCommand(executable, joined);
    }
}
