using System.Diagnostics;
using System.Text;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    /// <summary>How long output is still collected after the process exited (descendants may hold the pipes).</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private const string TruncatedMarker = "… [output truncated]";

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

        // Exited fires when the process ends. WaitForExitAsync would also wait for the output pipes to reach
        // EOF, which never happens while a background grand-child (a hook's daemon…) keeps them open.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();

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

        if (process.HasExited)
        {
            exited.TrySetResult();
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutCts = spec.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Standard input is fed while waiting, so the timeout and cancellation also cover a child that reads it slowly (or never).
        var feed = FeedStandardInputAsync(process, spec.StandardInput, linked.Token);
        try
        {
            await exited.Task.WaitAsync(linked.Token).ConfigureAwait(false);
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
        await Task.WhenAny(Task.WhenAll(stdoutDone.Task, stderrDone.Task, feed), Task.Delay(DrainTimeout, CancellationToken.None)).ConfigureAwait(false);
        stopwatch.Stop();

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed, timedOut);
    }

    /// <summary>Writes the optional input then closes stdin. Never throws: a child exiting before reading it is normal.</summary>
    private static async Task FeedStandardInputAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        var stdin = process.StandardInput;
        try
        {
            if (input is not null)
            {
                await stdin.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The process exited before reading its input (the exit code tells the story), or we gave up on it.
            // Release our end of the pipe without flushing the writer: a flush could block on a full pipe.
            // When we gave up, kill first: closing the pipe of a live process would let it take a partial
            // input as complete (e.g. commit a truncated message).
            if (ex is OperationCanceledException)
            {
                KillTree(process);
            }

            try
            {
                stdin.BaseStream.Dispose();
            }
            catch (Exception disposeError) when (disposeError is IOException or ObjectDisposedException)
            {
            }
        }
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

                    // NUL-separated output (-z) arrives as one huge "line": keep the whole records that fit.
                    var room = Math.Min(maxChars - _builder.Length - 1, line.Length);
                    var lastRecordEnd = room > 0 ? line.LastIndexOf('\0', room - 1) : -1;
                    if (lastRecordEnd >= 0)
                    {
                        _builder.Append(line, 0, lastRecordEnd + 1);
                    }

                    // '\n', not AppendLine: callers detect truncation by the marker followed by a line feed.
                    _builder.Append(TruncatedMarker).Append('\n');
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
