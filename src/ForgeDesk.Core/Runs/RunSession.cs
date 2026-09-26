using System.Diagnostics;
using System.Threading.Channels;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Processes;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// One command execution. Two reader loops (stdout, stderr) feed a single pump through a bounded
/// channel: lines get their index, reach the log file, the ring buffer, the progress and error
/// detectors and the subscribers in one well-defined order, and no lock is held while events
/// fire. When the channel is full the readers wait, which in turn slows the child process down
/// instead of letting memory grow.
/// </summary>
internal sealed class RunSession : IRunSession
{
    private const int ChannelCapacity = 4096;
    private const int ReadBufferSize = 16 * 1024;

    private readonly Lock _gate = new();
    private readonly RunLogBuffer _buffer;
    private readonly RunLogWriter _log;
    private readonly ProgressTracker _progress = new();
    private readonly ErrorSummaryExtractor _errors = new();
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _drainTimeout;
    private readonly Func<RunSession, RunRecord, Task> _onFinished;
    private readonly TaskCompletionSource<RunRecord> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RunProcess? _process;
    private bool _cancelRequested;
    private bool _exited;
    private RunStatus _status = RunStatus.Running;
    private DateTimeOffset? _endedAt;
    private int? _exitCode;
    private string? _errorSummary;
    private double? _progressValue;
    private DateTimeOffset _lastOutputAt;

    public RunSession(
        string id,
        RunRequest request,
        DateTimeOffset startedAt,
        RunLogWriter log,
        IClock clock,
        ILogger logger,
        Func<RunSession, RunRecord, Task> onFinished,
        int bufferCapacity,
        TimeSpan drainTimeout)
    {
        Id = id;
        Request = request;
        StartedAt = startedAt;
        _lastOutputAt = startedAt;
        _log = log;
        _clock = clock;
        _logger = logger;
        _onFinished = onFinished;
        _drainTimeout = drainTimeout;
        _buffer = new RunLogBuffer(bufferCapacity);
    }

    public string Id { get; }

    public RunRequest Request { get; }

    public DateTimeOffset StartedAt { get; }

    public string LogPath => _log.Path;

    public RunStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public DateTimeOffset? EndedAt
    {
        get
        {
            lock (_gate)
            {
                return _endedAt;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate)
            {
                return _exitCode;
            }
        }
    }

    public double? Progress
    {
        get
        {
            lock (_gate)
            {
                return _progressValue;
            }
        }
    }

    public DateTimeOffset LastOutputAt
    {
        get
        {
            lock (_gate)
            {
                return _lastOutputAt;
            }
        }
    }

    public int LineCount => (int)Math.Min(_buffer.TotalCount, int.MaxValue);

    public Task<RunRecord> Completion => _completion.Task;

    public event EventHandler<RunLogLine>? LineReceived;

    public event EventHandler? StatusChanged;

    public event EventHandler? ProgressChanged;

    public IReadOnlyList<RunLogLine> GetLines(long fromIndex = 0) => _buffer.GetFrom(fromIndex);

    /// <summary>Starts the process. Never throws: a start failure ends the run as Failed.</summary>
    public void Start(ProcessSpec spec)
    {
        RunProcess process;
        try
        {
            process = RunProcess.Start(spec);
        }
        catch (Exception ex)
        {
            var error = ex as ForgeException ?? new ForgeException(ErrorKind.ProcessFailed,
                "The command could not be started.", null, ex.Message, ex);
            _ = Task.Run(() => FinishAsync(exitCode: null, error));
            return;
        }

        bool cancelBeforeStart;
        lock (_gate)
        {
            _process = process;
            cancelBeforeStart = _cancelRequested;
        }

        if (cancelBeforeStart)
        {
            process.KillTree();
        }

        _ = Task.Run(() => MonitorAsync(process));
    }

    /// <summary>Requests cancellation (kills the process tree). Returns false when already finished.</summary>
    public bool Cancel()
    {
        RunProcess? process;
        lock (_gate)
        {
            if (_status is not RunStatus.Running and not RunStatus.Queued || _cancelRequested || _exited)
            {
                return false;
            }

            _cancelRequested = true;
            process = _process;
        }

        process?.KillTree();
        return true;
    }

    public void FlushLog() => _log.Flush();

    public RunRecord ToRecord()
    {
        lock (_gate)
        {
            return new RunRecord
            {
                Id = Id,
                ProjectId = Request.ProjectId,
                CommandId = Request.CommandId,
                Label = Request.Label,
                CommandLine = Request.CommandLine,
                WorkingDirectory = Request.WorkingDirectory,
                Category = Request.Category,
                Status = _status,
                StartedAt = StartedAt,
                EndedAt = _endedAt,
                ExitCode = _exitCode,
                LogPath = _log.Path,
                ErrorSummary = _errorSummary,
                LineCount = LineCount,
            };
        }
    }

    private async Task MonitorAsync(RunProcess process)
    {
        int? exitCode = null;
        try
        {
            var channel = Channel.CreateBounded<OutputItem>(new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            using var readCancellation = new CancellationTokenSource();
            var readers = Task.WhenAll(
                ReadStreamAsync(process.StandardOutput, isError: false, channel.Writer, readCancellation.Token),
                ReadStreamAsync(process.StandardError, isError: true, channel.Writer, readCancellation.Token));
            var pump = PumpAsync(channel.Reader);

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _exited = true;
            }

            // A background grandchild ("start /b server", "cmd &") can keep the pipes open forever.
            if (await Task.WhenAny(readers, Task.Delay(_drainTimeout, CancellationToken.None)).ConfigureAwait(false) != readers)
            {
                _logger.LogInformation("Run {RunId}: output still open after exit; detaching from it", Id);
                await readCancellation.CancelAsync().ConfigureAwait(false);
            }

            channel.Writer.TryComplete();
            await pump.ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId}: monitoring failed", Id);
        }
        finally
        {
            lock (_gate)
            {
                _process = null;
            }

            process.Dispose();
        }

        await FinishAsync(exitCode, startError: null).ConfigureAwait(false);
    }

    private static async Task ReadStreamAsync(Stream stream, bool isError, ChannelWriter<OutputItem> writer, CancellationToken cancellationToken)
    {
        var splitter = new OutputLineSplitter();
        var segments = new List<OutputSegment>();
        var buffer = new byte[ReadBufferSize];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                splitter.Feed(buffer.AsSpan(0, read), segments);
                await WriteAsync(segments, isError, writer, cancellationToken).ConfigureAwait(false);
            }

            splitter.Complete(segments);
            await WriteAsync(segments, isError, writer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or ChannelClosedException)
        {
            // Detached after exit or the pipe broke: whatever was read has been delivered.
        }
    }

    private static async ValueTask WriteAsync(List<OutputSegment> segments, bool isError, ChannelWriter<OutputItem> writer, CancellationToken cancellationToken)
    {
        foreach (var segment in segments)
        {
            await writer.WriteAsync(new OutputItem(segment.Text, isError, segment.IsTransient), cancellationToken).ConfigureAwait(false);
        }

        segments.Clear();
    }

    private async Task PumpAsync(ChannelReader<OutputItem> reader)
    {
        await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (item.IsTransient)
                {
                    lock (_gate)
                    {
                        _lastOutputAt = _clock.Now;
                    }

                    UpdateProgress(item.Text);
                }
                else
                {
                    AppendLine(item.Text, item.IsError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Run {RunId}: failed to process an output line", Id);
            }
        }
    }

    /// <summary>Only called from the pump, or after it completed: detectors are single-threaded.</summary>
    private void AppendLine(string text, bool isError)
    {
        var now = _clock.Now;
        var line = _buffer.Add(now, isError, text);
        _log.Append(line);
        _errors.Observe(text);
        lock (_gate)
        {
            _lastOutputAt = now;
        }

        SafeEvent.Raise(LineReceived, this, line);
        UpdateProgress(text);
    }

    private void UpdateProgress(string text)
    {
        if (!_progress.Observe(text))
        {
            return;
        }

        lock (_gate)
        {
            _progressValue = _progress.Current;
        }

        Raise(ProgressChanged);
    }

    private async Task FinishAsync(int? exitCode, ForgeException? startError)
    {
        RunRecord record;
        try
        {
            bool cancelled;
            lock (_gate)
            {
                cancelled = _cancelRequested;
            }

            RunStatus status;
            string? summary;
            if (startError is not null)
            {
                AppendLine($"Could not start the command: {startError.Message}", isError: true);
                if (startError.Hint is { Length: > 0 } hint)
                {
                    AppendLine(hint, isError: true);
                }

                status = RunStatus.Failed;
                summary = startError.Message;
            }
            else if (cancelled)
            {
                AppendLine("Command cancelled.", isError: false);
                status = RunStatus.Cancelled;
                summary = null;
            }
            else
            {
                status = exitCode == 0 ? RunStatus.Succeeded : RunStatus.Failed;
                summary = _errors.Build(failed: status == RunStatus.Failed);
            }

            lock (_gate)
            {
                _status = status;
                _endedAt = _clock.Now;
                _exitCode = exitCode;
                _errorSummary = summary;
            }

            record = ToRecord();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId}: failed to finalize", Id);
            lock (_gate)
            {
                _status = RunStatus.Failed;
                _endedAt ??= _clock.Now;
                _exitCode = exitCode;
            }

            record = ToRecord();
        }
        finally
        {
            _log.Dispose();
        }

        Raise(StatusChanged);
        try
        {
            await _onFinished(this, record).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId}: failed to save the result", Id);
        }

        _completion.TrySetResult(record);
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var single in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                single(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Event subscriber failed: {ex}");
            }
        }
    }

    private readonly record struct OutputItem(string Text, bool IsError, bool IsTransient);
}
