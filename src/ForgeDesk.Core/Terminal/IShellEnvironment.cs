using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;

namespace ForgeDesk.Core.Terminal;

/// <summary>
/// What shell discovery needs from the machine. Abstracted so the Windows layout (Program Files,
/// System32, WSL) can be simulated in tests on any operating system.
/// </summary>
internal interface IShellEnvironment
{
    bool IsWindows { get; }

    string? GetEnvironmentVariable(string name);

    bool FileExists(string path);

    /// <summary>Resolves a program name through PATH (and PATHEXT on Windows).</summary>
    string? FindInPath(string name);

    /// <summary>Runs a program and returns its standard output, or null when it failed or timed out.</summary>
    Task<string?> CaptureOutputAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken);
}

internal sealed class SystemShellEnvironment(IProcessRunner runner) : IShellEnvironment
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    public bool IsWindows => OperatingSystem.IsWindows();

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public bool FileExists(string path) => File.Exists(path);

    public string? FindInPath(string name) => ExecutableLocator.Find(name);

    public async Task<string?> CaptureOutputAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(new ProcessSpec
            {
                FileName = fileName,
                Arguments = arguments,
                Environment = environment,
                Timeout = ProbeTimeout,
                MaxCapturedChars = 256 * 1024,
            }, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.StandardOutput : null;
        }
        catch (ForgeException)
        {
            return null;
        }
    }
}
