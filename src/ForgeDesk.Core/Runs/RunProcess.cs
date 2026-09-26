using System.ComponentModel;
using System.Diagnostics;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// A long-lived child process whose raw output streams are read by the caller. Unlike
/// <see cref="ProcessRunner"/>, it exposes bytes (to handle "\r" redraws and mixed encodings)
/// and keeps standard input open: closing it makes watch-mode tools such as esbuild exit
/// immediately, while nothing is ever typed into a run.
/// </summary>
internal sealed class RunProcess : IDisposable
{
    private readonly Process _process;

    private RunProcess(Process process)
    {
        _process = process;
    }

    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public Stream StandardError => _process.StandardError.BaseStream;

    public int Id => _process.Id;

    /// <summary>Starts the process. Throws <see cref="ForgeException"/> (PathNotFound, ToolNotFound) when it cannot start.</summary>
    public static RunProcess Start(ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.WorkingDirectory is { } workingDirectory && !Directory.Exists(workingDirectory))
        {
            throw new ForgeException(ErrorKind.PathNotFound,
                $"The folder '{workingDirectory}' does not exist.",
                "The project may have been moved or deleted. Check the command's working directory.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (spec.RawArguments is not null)
        {
            startInfo.Arguments = spec.RawArguments;
        }
        else
        {
            foreach (var argument in spec.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        foreach (var (key, value) in spec.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(key);
            }
            else
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ForgeException(ErrorKind.ToolNotFound, $"'{Path.GetFileName(spec.FileName)}' could not be started.");
            }
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            throw new ForgeException(ErrorKind.ToolNotFound,
                $"The command shell '{Path.GetFileName(spec.FileName)}' could not be started.",
                "Make sure it is installed and that ForgeDesk is allowed to run programs.", ex.Message, ex);
        }
        catch (ForgeException)
        {
            process.Dispose();
            throw;
        }

        return new RunProcess(process);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Kills the process and all of its descendants (npm → node → esbuild…).</summary>
    public void KillTree() => ProcessRunner.KillTree(_process);

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Dispose();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The pipe is already broken: the process is gone.
        }

        _process.Dispose();
    }
}
