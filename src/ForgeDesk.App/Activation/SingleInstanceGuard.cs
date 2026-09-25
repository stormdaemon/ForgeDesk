using System.IO;

namespace ForgeDesk.App.Activation;

/// <summary>
/// Owns the named mutex that marks the primary ForgeDesk instance for the current user.
/// Dispose it on the thread that created it (the mutex has thread affinity).
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstanceGuard(Mutex? mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    /// <summary>True for the first instance, which must serve activations from later ones.</summary>
    public bool IsPrimary { get; }

    public static SingleInstanceGuard Acquire(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Without a usable mutex it is better to run than to refuse to start.
            return new SingleInstanceGuard(null, isPrimary: true);
        }

        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous primary instance crashed; this one takes over.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return new SingleInstanceGuard(null, isPrimary: false);
        }

        return new SingleInstanceGuard(mutex, isPrimary: true);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread any more; disposing still closes the handle.
        }

        _mutex.Dispose();
    }
}
