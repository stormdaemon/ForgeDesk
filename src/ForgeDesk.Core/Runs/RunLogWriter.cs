using System.Text;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Runs;

/// <summary>
/// Appends run output to the log file through a buffer flushed periodically (so the file is
/// always nearly current for viewers) and on dispose. Failures (disk full, file deleted) disable
/// the log instead of breaking the run; the in-memory buffer keeps working.
/// </summary>
internal sealed class RunLogWriter : IDisposable
{
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Protects the disk from a process that prints forever (a dev server left running for weeks).</summary>
    public const long DefaultMaxBytes = 200L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private readonly long _maxBytes;
    private readonly Timer _flushTimer;
    private StreamWriter? _writer;
    private long _bytesWritten;
    private bool _dirty;
    private bool _limitReached;

    private RunLogWriter(string path, StreamWriter? writer, TimeSpan flushInterval, long maxBytes, ILogger logger)
    {
        Path = path;
        _writer = writer;
        _maxBytes = maxBytes;
        _logger = logger;
        _flushTimer = new Timer(static state => ((RunLogWriter)state!).Flush(), this, flushInterval, flushInterval);
    }

    public string Path { get; }

    /// <summary>Opens (creating folders) the log file. Never throws: an unusable file yields a disabled writer.</summary>
    public static RunLogWriter Open(string path, ILogger logger, TimeSpan? flushInterval = null, long maxBytes = DefaultMaxBytes)
    {
        StreamWriter? writer = null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, 64 * 1024);
            writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024) { NewLine = "\n", AutoFlush = false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not create run log {Path}; output is kept in memory only", path);
        }

        return new RunLogWriter(path, writer, flushInterval ?? DefaultFlushInterval, maxBytes, logger);
    }

    public void Append(RunLogLine line)
    {
        lock (_gate)
        {
            if (_writer is null || _limitReached)
            {
                return;
            }

            try
            {
                var formatted = RunLogFormat.Format(line);
                if (_bytesWritten + formatted.Length > _maxBytes)
                {
                    _limitReached = true;
                    _writer.WriteLine(RunLogFormat.Format(line with
                    {
                        IsError = true,
                        Text = "[ForgeDesk] The log file reached its size limit; later output is only shown live.",
                    }));
                }
                else
                {
                    _writer.WriteLine(formatted);
                    _bytesWritten += formatted.Length + 1;
                }

                _dirty = true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Disable(ex);
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_writer is null || !_dirty)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Disable(ex);
            }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        lock (_gate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Dispose();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not finish writing run log {Path}", Path);
            }

            _writer = null;
        }
    }

    private void Disable(Exception ex)
    {
        _logger.LogWarning(ex, "Writing run log {Path} failed; output is kept in memory only", Path);
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }

        _writer = null;
    }
}
