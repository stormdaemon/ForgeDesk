namespace ForgeDesk.Core.Terminal;

public enum ShellKind
{
    PowerShellCore,
    WindowsPowerShell,
    CommandPrompt,
    GitBash,
    Wsl,
    Unix,
    Custom,
}

/// <summary>A shell ForgeDesk can open in its integrated terminal.</summary>
public sealed record ShellProfile(string Id, string Name, ShellKind Kind, string Executable, string Arguments)
{
    public string CommandLine => string.IsNullOrWhiteSpace(Arguments) ? Quote(Executable) : $"{Quote(Executable)} {Arguments}";

    private static string Quote(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}

public interface IShellDiscovery
{
    /// <summary>Installed shells, best first (PowerShell 7, Windows PowerShell, cmd, Git Bash, WSL).</summary>
    Task<IReadOnlyList<ShellProfile>> DiscoverAsync(CancellationToken cancellationToken = default);
}
