using System.IO;
using ForgeDesk.Core.Common;

namespace ForgeDesk.App.Activation;

/// <summary>
/// What ForgeDesk was asked to open when launched (or re-launched): a registered project
/// (<c>--open-project &lt;id&gt;</c>, used by the jump list and toasts) and/or a folder path
/// (<c>ForgeDesk.exe C:\dev\app</c>, <c>ForgeDesk .</c> from a terminal).
/// </summary>
public sealed record ActivationRequest(string? ProjectId, string? FolderPath, IReadOnlyList<string> Arguments)
{
    public const string OpenProjectOption = "--open-project";

    public static readonly ActivationRequest Empty = new(null, null, []);

    public bool IsEmpty => ProjectId is null && FolderPath is null;

    public static ActivationRequest ForProject(string projectId) => new(projectId, null, [OpenProjectOption, projectId]);

    /// <summary>
    /// Parses command-line arguments. Relative folders are resolved against
    /// <paramref name="workingDirectory"/> (the caller's, for a forwarded second instance).
    /// Unknown switches (Windows adds -ToastActivated or -Embedding) are ignored.
    /// </summary>
    public static ActivationRequest Parse(IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? projectId = null;
        string? folder = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i]?.Trim() ?? string.Empty;
            if (argument.Length == 0)
            {
                continue;
            }

            if (argument.Equals(OpenProjectOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < arguments.Count)
                {
                    projectId ??= CleanId(arguments[++i]);
                }

                continue;
            }

            if (argument.StartsWith(OpenProjectOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                projectId ??= CleanId(argument[(OpenProjectOption.Length + 1)..]);
                continue;
            }

            if (argument.StartsWith('-'))
            {
                continue;
            }

            folder ??= ResolveFolder(argument, workingDirectory);
        }

        return new ActivationRequest(projectId, folder, arguments.ToArray());
    }

    private static string? CleanId(string? value)
    {
        var id = value?.Trim().Trim('"');
        return string.IsNullOrEmpty(id) || id.Any(char.IsWhiteSpace) || id.StartsWith('-') ? null : id;
    }

    private static string? ResolveFolder(string argument, string workingDirectory)
    {
        try
        {
            var candidate = argument.Trim('"');
            var combined = Path.IsPathRooted(candidate) || string.IsNullOrWhiteSpace(workingDirectory)
                ? candidate
                : Path.Combine(workingDirectory, candidate);
            return PathUtil.Normalize(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
