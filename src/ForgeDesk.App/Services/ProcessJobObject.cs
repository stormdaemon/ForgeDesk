using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ForgeDesk.App.Native;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.App.Services;

/// <summary>
/// Places the ForgeDesk process in a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Child
/// processes (commands, shells, git) inherit the job, so they all die when ForgeDesk exits or
/// crashes and the OS closes the last job handle — no orphaned dev servers holding ports.
/// Programs the user keeps using (editor, browser) break away through
/// <see cref="Launching.DetachedProcessLauncher"/>, which is why breakaway is allowed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessJobObject
{
    private readonly ILogger<ProcessJobObject> _logger;
    private readonly Lock _gate = new();

    // Never closed explicitly: closing the last handle kills every process in the job,
    // ForgeDesk included. The static root keeps the SafeHandle's finalizer from running
    // before the process ends, even after the DI container is disposed.
    private static SafeKernelHandle? s_job;

    public ProcessJobObject(ILogger<ProcessJobObject> logger) => _logger = logger;

    /// <summary>True once the current process runs inside the kill-on-close job.</summary>
    public bool IsActive => s_job is not null;

    /// <summary>Creates the job and assigns the current process to it. Safe to call more than once.</summary>
    public void AttachCurrentProcess()
    {
        lock (_gate)
        {
            if (IsActive)
            {
                return;
            }

            var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                LogFailure("create the job object", Marshal.GetLastPInvokeError());
                job.Dispose();
                return;
            }

            if (!SetLimits(job, NativeMethods.JobObjectLimitKillOnJobClose | NativeMethods.JobObjectLimitBreakawayOk))
            {
                job.Dispose();
                return;
            }

            if (!NativeMethods.AssignProcessToJobObject(job, NativeMethods.GetCurrentProcess()))
            {
                // Typical when an outer job (older Windows, some CI agents) forbids nesting.
                // ForgeDesk still works; child processes just are not cleaned up on crash.
                LogFailure("join the job object", Marshal.GetLastPInvokeError());
                job.Dispose();
                return;
            }

            s_job = job;
            _logger.LogInformation("Child processes are bound to the ForgeDesk job object");
        }
    }

    /// <summary>
    /// Lets child processes outlive ForgeDesk. Call right before handing over to an updater
    /// that must keep running after ForgeDesk exits (Velopack's apply-and-restart).
    /// </summary>
    public void ReleaseChildrenOnExit()
    {
        lock (_gate)
        {
            if (s_job is { IsInvalid: false } job && SetLimits(job, NativeMethods.JobObjectLimitBreakawayOk))
            {
                _logger.LogInformation("Child processes will no longer be terminated when ForgeDesk exits");
            }
        }
    }

    private bool SetLimits(SafeKernelHandle job, uint limitFlags)
    {
        var info = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation { LimitFlags = limitFlags },
        };

        if (NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformationClass, ref info,
                (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
        {
            return true;
        }

        LogFailure("configure the job object", Marshal.GetLastPInvokeError());
        return false;
    }

    private void LogFailure(string action, int error) =>
        _logger.LogWarning("Could not {Action}: {Message} (error {Error})", action, new Win32Exception(error).Message, error);
}
