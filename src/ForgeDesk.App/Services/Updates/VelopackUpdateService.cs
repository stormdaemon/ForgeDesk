using ForgeDesk.App.Startup;
using ForgeDesk.Core.Common;
using ForgeDesk.Presentation.Settings;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace ForgeDesk.App.Services.Updates;

/// <summary>
/// Updates from the GitHub releases of ForgeDesk through Velopack. Only an installed copy (Setup)
/// updates itself; portable copies and development builds report <see cref="IsSupported"/> false.
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService
{
    public const string RepositoryUrl = "https://github.com/stormdaemon/ForgeDesk";

    private readonly ProcessJobObject _jobObject;
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly Lazy<UpdateManager?> _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateService(ProcessJobObject jobObject, ILogger<VelopackUpdateService> logger)
    {
        _jobObject = jobObject;
        _logger = logger;
        _manager = new Lazy<UpdateManager?>(CreateManager, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsSupported => _manager.Value is { IsInstalled: true, IsPortable: false };

    public string CurrentVersion => _manager.Value?.CurrentVersion?.ToString() is { Length: > 0 } installed ? installed : AppInfo.Version;

    public UpdateState State { get; private set; } = UpdateState.Idle;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var manager = RequireManager();
        State = UpdateState.Checking;
        try
        {
            var info = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            _pending = info;
            if (info is null)
            {
                State = UpdateState.Idle;
                return UpdateCheckResult.UpToDate;
            }

            State = UpdateState.UpdateAvailable;
            var release = info.TargetFullRelease;
            _logger.LogInformation("Update {Version} found", release.Version);
            return new UpdateCheckResult(true, release.Version.ToString(), release.NotesMarkdown);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ForgeException)
        {
            State = UpdateState.Failed;
            throw new ForgeException(ErrorKind.NetworkUnavailable, "ForgeDesk couldn't reach GitHub to check for updates.",
                "Check your internet connection, then try again.", ex.Message, ex);
        }
    }

    public async Task DownloadAndApplyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var manager = RequireManager();
        var info = _pending ?? await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            throw new ForgeException(ErrorKind.NotFound, "There is no update to install.", "You already have the latest version of ForgeDesk.");
        }

        State = UpdateState.Downloading;
        try
        {
            await manager.DownloadUpdatesAsync(info, percent => progress?.Report(percent / 100.0), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State = UpdateState.Failed;
            throw new ForgeException(ErrorKind.NetworkUnavailable, "The update could not be downloaded.",
                "Check your internet connection, then try again.", ex.Message, ex);
        }

        State = UpdateState.ReadyToApply;
        _logger.LogInformation("Applying update {Version} and restarting", info.TargetFullRelease.Version);

        // Velopack's updater must outlive ForgeDesk: leave the kill-on-close job first.
        _jobObject.ReleaseChildrenOnExit();
        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    private UpdateManager RequireManager() =>
        _manager.Value is { IsInstalled: true } manager
            ? manager
            : throw new ForgeException(ErrorKind.Unknown, "This copy of ForgeDesk can't update itself.",
                "Install ForgeDesk with its Setup program to receive updates.");

    private UpdateManager? CreateManager()
    {
        try
        {
            return new UpdateManager(new GithubSource(RepositoryUrl, null, false));
        }
        catch (Exception ex)
        {
            // Development builds and unusual layouts: updates are simply unavailable.
            _logger.LogInformation(ex, "Updates are unavailable in this copy of ForgeDesk");
            return null;
        }
    }
}
