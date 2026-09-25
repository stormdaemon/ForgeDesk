using ForgeDesk.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeDesk.Core.Files;

/// <summary>
/// One recursive <see cref="FileSystemWatcher"/> per open project. Relevant events are coalesced
/// per project (trailing debounce, with a maximum delay so a long build still refreshes now and
/// then) into a single <see cref="Changed"/> notification. A watcher buffer overflow means events
/// were lost: the project is reported as fully changed and the watcher recreated.
/// </summary>
internal sealed class ProjectWatcher : IProjectWatcher
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    private const int BufferSize = 64 * 1024;

    private readonly Dictionary<string, WatchedProject> _projects = new(PathUtil.Comparer);
    private readonly Lock _gate = new();
    private readonly ILogger _logger;
    private bool _disposed;

    public ProjectWatcher(ILogger<ProjectWatcher>? logger = null)
        : this(DefaultDebounce, logger)
    {
    }

    internal ProjectWatcher(TimeSpan debounce, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(debounce, TimeSpan.Zero);
        Debounce = debounce;
        MaxDelay = TimeSpan.FromTicks(Math.Max(debounce.Ticks * 7, TimeSpan.FromSeconds(2).Ticks));
        _logger = logger ?? NullLogger.Instance;
    }

    public event EventHandler<ProjectFilesChangedEventArgs>? Changed;

    internal TimeSpan Debounce { get; }

    /// <summary>Longest time a burst of events can postpone the notification.</summary>
    internal TimeSpan MaxDelay { get; }

    internal IReadOnlyCollection<string> WatchedRoots
    {
        get
        {
            lock (_gate)
            {
                return [.. _projects.Keys];
            }
        }
    }

    public void Watch(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = PathUtil.Normalize(projectRoot);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_projects.ContainsKey(root))
            {
                return;
            }

            if (!Directory.Exists(root))
            {
                _logger.LogInformation("Not watching {Root}: the folder does not exist", root);
                return;
            }

            var project = new WatchedProject(root, this);
            if (project.Start())
            {
                _projects[root] = project;
            }
            else
            {
                project.Dispose();
            }
        }
    }

    public void Unwatch(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        WatchedProject? project;
        lock (_gate)
        {
            if (!_projects.Remove(PathUtil.Normalize(projectRoot), out project))
            {
                return;
            }
        }

        project.Dispose();
    }

    public void Dispose()
    {
        List<WatchedProject> projects;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            projects = [.. _projects.Values];
            _projects.Clear();
        }

        foreach (var project in projects)
        {
            project.Dispose();
        }
    }

    private void Raise(ProjectFilesChangedEventArgs args) => SafeEvent.Raise(Changed, this, args);

    private sealed class WatchedProject : IDisposable
    {
        private readonly string _root;
        private readonly ProjectWatcher _owner;
        private readonly Lock _gate = new();
        private readonly Timer _timer;
        private FileSystemWatcher? _watcher;
        private bool _gitChanged;
        private bool _treeChanged;
        private long? _firstPendingAt;
        private bool _disposed;

        public WatchedProject(string root, ProjectWatcher owner)
        {
            _root = root;
            _owner = owner;
            _timer = new Timer(static state => ((WatchedProject)state!).Flush(), this, Timeout.Infinite, Timeout.Infinite);
        }

        public bool Start()
        {
            lock (_gate)
            {
                _watcher = CreateWatcher();
                return _watcher is not null;
            }
        }

        public void Dispose()
        {
            FileSystemWatcher? watcher;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                watcher = _watcher;
                _watcher = null;
            }

            watcher?.Dispose();
            _timer.Dispose();
        }

        private FileSystemWatcher? CreateWatcher()
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = BufferSize,
                };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Changed += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Missing folder, exhausted OS watch handles, network share without notifications…
                _owner._logger.LogWarning(ex, "Could not watch {Root} for changes", _root);
                watcher?.Dispose();
                return null;
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            var isFolderContentChange = e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath);
            Record(WatchedPathClassifier.Classify(RelativeName(e.Name, e.FullPath), isFolderContentChange));
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Record(WatchedPathClassifier.Classify(RelativeName(e.OldName, e.OldFullPath)));
            Record(WatchedPathClassifier.Classify(RelativeName(e.Name, e.FullPath)));
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            _owner._logger.LogInformation(e.GetException(), "File watcher for {Root} lost events; refreshing everything", _root);
            Schedule(git: true, tree: true);
            Recreate();
        }

        private void Recreate()
        {
            FileSystemWatcher? old;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                old = _watcher;
                _watcher = null;
            }

            old?.Dispose();
            if (!Directory.Exists(_root))
            {
                _owner._logger.LogInformation("{Root} no longer exists; it is not watched anymore", _root);
                return;
            }

            var replacement = CreateWatcher();
            lock (_gate)
            {
                if (_disposed)
                {
                    replacement?.Dispose();
                    return;
                }

                _watcher = replacement;
            }
        }

        private string RelativeName(string? name, string fullPath) =>
            !string.IsNullOrEmpty(name) ? name : Path.GetRelativePath(_root, fullPath);

        private void Record(WatchedChangeKind kind)
        {
            switch (kind)
            {
                case WatchedChangeKind.GitMetadata:
                    Schedule(git: true, tree: false);
                    break;
                case WatchedChangeKind.WorkingTree:
                    Schedule(git: false, tree: true);
                    break;
            }
        }

        private void Schedule(bool git, bool tree)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _gitChanged |= git;
                _treeChanged |= tree;
                var now = Environment.TickCount64;
                _firstPendingAt ??= now;

                var waited = TimeSpan.FromMilliseconds(now - _firstPendingAt.Value);
                var due = _owner.Debounce;
                if (waited + due > _owner.MaxDelay)
                {
                    due = _owner.MaxDelay - waited;
                    if (due < TimeSpan.Zero)
                    {
                        due = TimeSpan.Zero;
                    }
                }

                _timer.Change(due, Timeout.InfiniteTimeSpan);
            }
        }

        private void Flush()
        {
            bool git;
            bool tree;
            lock (_gate)
            {
                if (_disposed || (!_gitChanged && !_treeChanged))
                {
                    return;
                }

                git = _gitChanged;
                tree = _treeChanged;
                _gitChanged = false;
                _treeChanged = false;
                _firstPendingAt = null;
            }

            _owner.Raise(new ProjectFilesChangedEventArgs(_root, git, tree));
        }
    }
}
