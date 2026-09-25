using System.Diagnostics;
using System.Text;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) =>
        RunAsync(spec, static _ => { }, cancellationToken);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputLine> onOutput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(onOutput);
        cancellationToken.ThrowIfCancellationRequested();

        if (spec.WorkingDirectory is { } wd && !Directory.Exists(wd))
        {
            throw new ForgeException(ErrorKind.PathNotFound, $"The folder '{wd}' does not exist.",
                "The project may have been moved or deleted.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (spec.StandardInput is not null)
        {
            psi.StandardInputEncoding = new UTF8Encoding(false);
        }

        if (spec.RawArguments is not null)
        {
            psi.Arguments = spec.RawArguments;
        }
        else
        {
            foreach (var arg in spec.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        foreach (var (key, value) in spec.Environment)
        {
            if (value is null)
            {
                psi.Environment.Remove(key);
            }
            else
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new CappedBuilder(spec.MaxCapturedChars);
        var stderr = new CappedBuilder(spec.MaxCapturedChars);
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutDone.TrySetResult();
                return;
            }

            stdout.AppendLine(e.Data);
            SafeInvoke(onOutput, new OutputLine(OutputStream.StandardOutput, e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.TrySetResult();
                return;
            }

            stderr.AppendLine(e.Data);
            SafeInvoke(onOutput, new OutputLine(OutputStream.StandardError, e.Data));
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                throw new ForgeException(ErrorKind.ToolNotFound, $"Could not start '{spec.FileName}'.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ForgeException(ErrorKind.ToolNotFound,
                $"'{Path.GetFileName(spec.FileName)}' could not be started.",
                "Make sure the program is installed and available in your PATH.", ex.Message, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            if (spec.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited before reading its input; the exit code tells the story.
        }

        var timedOut = false;
        using var timeoutCts = spec.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        // Drain the output pipes, but never hang forever on grand-children keeping them open.
        await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        stopwatch.Stop();

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed, timedOut);
    }

    /// <summary>Kills a process and all of its descendants, swallowing races with natural exit.</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void SafeInvoke(Action<OutputLine> callback, OutputLine line)
    {
        try
        {
            callback(line);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Output callback failed: {ex}");
        }
    }

    private sealed class CappedBuilder(int maxChars)
    {
        private readonly StringBuilder _builder = new();
        private readonly Lock _gate = new();
        private bool _truncated;

        public void AppendLine(string line)
        {
            lock (_gate)
            {
                if (_truncated)
                {
                    return;
                }

                if (_builder.Length + line.Length + 1 > maxChars)
                {
                    _truncated = true;
                    _builder.AppendLine("… [output truncated]");
                    return;
                }

                _builder.Append(line).Append('\n');
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _builder.ToString();
            }
        }
    }
}
