using System.Reflection;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Presentation.Settings;

public enum UpdateState
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,

    /// <summary>The update is downloaded and applies when ForgeDesk restarts.</summary>
    ReadyToApply,
    Failed,
}

/// <summary>Outcome of an update check.</summary>
public sealed record UpdateCheckResult(bool Available, string? Version, string? Notes)
{
    public static UpdateCheckResult UpToDate { get; } = new(false, null, null);
}

/// <summary>
/// Application self-update (Velopack in the app). <see cref="IsSupported"/> is false when ForgeDesk
/// was not installed with its Setup program (portable copy, development build): updates are then
/// neither checked nor offered.
/// </summary>
public interface IUpdateService
{
    bool IsSupported { get; }

    /// <summary>Version of the running ForgeDesk ("1.2.0").</summary>
    string CurrentVersion { get; }

    UpdateState State { get; }

    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the update found by the last check (progress 0..1), then exits ForgeDesk, applies it
    /// and restarts. Returns only when it could not restart.
    /// </summary>
    Task DownloadAndApplyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Stand-in used when the host provides no updater (tests, other hosts).</summary>
public sealed class UnavailableUpdateService : IUpdateService
{
    public bool IsSupported => false;

    public string CurrentVersion { get; } = ProductVersion.Of(typeof(UnavailableUpdateService).Assembly);

    public UpdateState State => UpdateState.Idle;

    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<UpdateCheckResult>(NotSupported());

    public Task DownloadAndApplyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.FromException(NotSupported());

    private static ForgeException NotSupported() => new(ErrorKind.Unknown, "This copy of ForgeDesk can't update itself.",
        "Install ForgeDesk with its Setup program to receive updates.");
}

/// <summary>Reads the product version of an assembly.</summary>
public static class ProductVersion
{
    /// <summary>The informational version without build metadata ("1.2.0", not "1.2.0+3f2c…").</summary>
    public static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}
