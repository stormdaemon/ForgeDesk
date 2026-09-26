using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ForgeDesk.Core.Files;

/// <summary>
/// Content search without git: parallel reads of the project's text files (heavy folders,
/// binaries and files over 2 MB skipped), each regex evaluation bounded by a timeout so a
/// pathological pattern cannot hang the search.
/// </summary>
internal sealed class ManagedContentSearcher(ILogger logger)
{
    public const long MaxFileSize = 2 * 1024 * 1024;
    public const int MaxFilesScanned = 300_000;

    public async Task<SearchOutcome> SearchAsync(string root, SearchPattern pattern, PathFilter filter, int maxResults,
        Action<ContentMatch> onMatch, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gate = new Lock();
        var matches = 0;
        var files = 0;
        var truncated = false;
        var scanned = 0;

        var candidates = ProjectFileWalker.EnumerateFiles(root)
            .Where(filter.IsMatch)
            .TakeWhile(_ =>
            {
                if (++scanned <= MaxFilesScanned)
                {
                    return true;
                }

                truncated = true;
                return false;
            });

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = stop.Token,
        };

        try
        {
            await Parallel.ForEachAsync(candidates, options, async (relativePath, token) =>
            {
                var found = await SearchFileAsync(root, relativePath, pattern, maxResults, token).ConfigureAwait(false);
                if (found.Count == 0)
                {
                    return;
                }

                lock (gate)
                {
                    if (matches >= maxResults)
                    {
                        return;
                    }

                    files++;
                    foreach (var match in found)
                    {
                        Deliver(onMatch, match);
                        if (++matches >= maxResults)
                        {
                            truncated = true;
                            stop.Cancel();
                            break;
                        }
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Stopped because the result limit was reached.
        }

        return new SearchOutcome(matches, files, truncated);
    }

    private void Deliver(Action<ContentMatch> onMatch, ContentMatch match)
    {
        // Same contract as the git engine: a faulty consumer does not abort the search.
        try
        {
            onMatch(match);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search result consumer failed");
        }
    }

    private async Task<List<ContentMatch>> SearchFileAsync(string root, string relativePath, SearchPattern pattern, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<ContentMatch>();
        byte[] bytes;
        try
        {
            var fullPath = Path.Combine(root, relativePath);
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                if (stream.Length > MaxFileSize || stream.Length == 0)
                {
                    return results;
                }

                bytes = new byte[stream.Length];
                var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                if (read < bytes.Length)
                {
                    Array.Resize(ref bytes, read);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or vanished files are simply not searched.
            return results;
        }

        if (TextDecoding.DetectBom(bytes) is null && TextDecoding.LooksBinary(bytes))
        {
            return results;
        }

        var text = TextDecoding.Decode(bytes).Text;
        var lineNumber = 0;
        var start = 0;
        try
        {
            while (start < text.Length && results.Count < maxResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = text.IndexOf('\n', start);
                var lineEnd = end < 0 ? text.Length : end;
                if (lineEnd > start && text[lineEnd - 1] == '\r')
                {
                    lineEnd--;
                }

                lineNumber++;
                var line = text[start..lineEnd];

                // IsMatch first: cheap, and a timeout here skips the rest of the file.
                if (pattern.Regex.IsMatch(line))
                {
                    results.Add(pattern.CreateMatch(relativePath, lineNumber, line));
                }

                if (end < 0)
                {
                    break;
                }

                start = end + 1;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            logger.LogDebug("Search pattern timed out on {Path}; skipping the rest of the file", relativePath);
        }

        return results;
    }
}
