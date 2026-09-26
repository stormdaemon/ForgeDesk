using System.Collections.Concurrent;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.GitHub;

/// <summary>
/// Short-lived in-memory cache of GitHub responses, keyed by scope (a repository) and call.
/// It protects the rate limit when several views ask for the same data (dashboard cards,
/// status bar, the GitHub tab), and concurrent identical requests share one in-flight call.
/// Failures are never cached.
/// </summary>
internal sealed class GitHubResponseCache
{
    /// <summary>Scope for data that belongs to the account rather than a repository.</summary>
    public const string AccountScope = "~account";

    private const int MaxEntries = 512;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IClock _clock;

    public GitHubResponseCache(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Returns the cached value, joins an identical in-flight request, or runs <paramref name="factory"/>.
    /// The factory is shared between callers, so it must not observe any single caller's
    /// cancellation; each caller stops waiting when its own <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task<T> GetOrAddAsync<T>(string scope, string key, TimeSpan timeToLive, Func<Task<T>> factory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var normalizedScope = NormalizeScope(scope);
        var fullKey = $"{normalizedScope}\n{key}";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.TryGetValue(fullKey, out var existing))
            {
                if (!existing.IsExpired(_clock.Now))
                {
                    return (T)(await existing.Value.WaitAsync(cancellationToken).ConfigureAwait(false))!;
                }

                _entries.TryRemove(new KeyValuePair<string, Entry>(fullKey, existing));
                continue;
            }

            var entry = new Entry(normalizedScope);
            if (!_entries.TryAdd(fullKey, entry))
            {
                continue;
            }

            if (_entries.Count > MaxEntries)
            {
                Prune();
            }

            entry.Start(async () => await factory().ConfigureAwait(false), timeToLive, _clock, () => _entries.TryRemove(new KeyValuePair<string, Entry>(fullKey, entry)));
            return (T)(await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false))!;
        }
    }

    /// <summary>Forgets everything cached for a repository (after a write to it).</summary>
    public void Invalidate(string scope)
    {
        var normalized = NormalizeScope(scope);
        foreach (var (key, entry) in _entries)
        {
            if (string.Equals(entry.Scope, normalized, StringComparison.Ordinal))
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }
    }

    public void Clear() => _entries.Clear();

    // GitHub owner and repository names are case-insensitive.
    private static string NormalizeScope(string scope) => scope.ToLowerInvariant();

    private void Prune()
    {
        var now = _clock.Now;
        foreach (var (key, entry) in _entries)
        {
            if (entry.IsExpired(now))
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }

        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var (key, entry) in _entries)
        {
            if (entry.IsCompleted)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
        }
    }

    private sealed class Entry(string scope)
    {
        private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // UTC ticks, read without locks by other threads; MaxValue while the request runs.
        private long _expiresAtUtcTicks = long.MaxValue;

        public string Scope { get; } = scope;

        public Task<object?> Value => _completion.Task;

        public bool IsCompleted => _completion.Task.IsCompleted;

        /// <summary>In-flight entries never expire: late callers join the running request.</summary>
        public bool IsExpired(DateTimeOffset now) => now.UtcTicks >= Volatile.Read(ref _expiresAtUtcTicks);

        public void Start(Func<Task<object?>> factory, TimeSpan timeToLive, IClock clock, Action remove) =>
            _ = RunAsync(factory, timeToLive, clock, remove);

        private async Task RunAsync(Func<Task<object?>> factory, TimeSpan timeToLive, IClock clock, Action remove)
        {
            try
            {
                var value = await factory().ConfigureAwait(false);
                Volatile.Write(ref _expiresAtUtcTicks, (clock.Now + timeToLive).UtcTicks);
                _completion.TrySetResult(value);
            }
            catch (Exception ex)
            {
                remove();
                _completion.TrySetException(ex);

                // Every waiter may have given up (cancelled); don't report the failure as unobserved.
                _ = _completion.Task.Exception;
            }
        }
    }
}
