namespace ForgeDesk.Core.Processes;

public interface IProcessRunner
{
    /// <summary>
    /// Runs a process to completion, capturing its output. Cancellation and timeouts
    /// kill the whole process tree. Throws <see cref="Common.ForgeException"/> with
    /// <see cref="Common.ErrorKind.ToolNotFound"/> when the executable cannot be started.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="RunAsync"/> but streams every line as it arrives.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputLine> onOutput, CancellationToken cancellationToken = default);
}
