using System.Collections.Concurrent;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Files;

public class WatchedPathClassifierTests
{
    [Theory]
    [InlineData("src/app.cs", "WorkingTree")]
    [InlineData(@"src\app.cs", "WorkingTree")]
    [InlineData("README.md", "WorkingTree")]
    [InlineData("node_modules", "WorkingTree")]
    [InlineData("node_modules/react/index.js", "Ignored")]
    [InlineData("src/bin/Debug/app.dll", "Ignored")]
    [InlineData("OBJ/project.assets.json", "Ignored")]
    [InlineData("packages/ui/src/button.tsx", "WorkingTree")]
    [InlineData(".git", "GitMetadata")]
    [InlineData(".git/HEAD", "GitMetadata")]
    [InlineData(".git/index", "GitMetadata")]
    [InlineData(".git/packed-refs", "GitMetadata")]
    [InlineData(".git/FETCH_HEAD", "GitMetadata")]
    [InlineData(".git/MERGE_HEAD", "GitMetadata")]
    [InlineData(".git/refs/heads/feature/login", "GitMetadata")]
    [InlineData(@".git\refs\remotes\origin\main", "GitMetadata")]
    [InlineData(".git/objects/ab/cdef0123", "Ignored")]
    [InlineData(".git/logs/HEAD", "Ignored")]
    [InlineData(".git/index.lock", "Ignored")]
    [InlineData(".git/COMMIT_EDITMSG", "Ignored")]
    public void Classifies_paths(string path, string expected)
    {
        WatchedPathClassifier.Classify(path).ToString().Should().Be(expected);
    }

    [Fact]
    public void Content_changes_of_a_heavy_folder_itself_are_noise()
    {
        WatchedPathClassifier.Classify("node_modules", isFolderContentChange: true).Should().Be(WatchedChangeKind.Ignored);
        WatchedPathClassifier.Classify("src", isFolderContentChange: true).Should().Be(WatchedChangeKind.WorkingTree);
    }
}

public class ProjectWatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorder : IDisposable
    {
        private readonly ProjectWatcher _watcher;

        public Recorder(TimeSpan? debounce = null)
        {
            _watcher = new ProjectWatcher(debounce ?? TimeSpan.FromMilliseconds(200));
            _watcher.Changed += (_, e) => Events.Enqueue(e);
        }

        public ConcurrentQueue<ProjectFilesChangedEventArgs> Events { get; } = new();

        public ProjectWatcher Watcher => _watcher;

        public async Task<ProjectFilesChangedEventArgs> WaitForEventAsync(Func<ProjectFilesChangedEventArgs, bool>? predicate = null)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                var match = Events.FirstOrDefault(e => predicate?.Invoke(e) ?? true);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25, Ct);
            }

            throw new TimeoutException("No change notification was raised.");
        }

        /// <summary>Lets pending notifications fire, then drops them.</summary>
        public async Task SettleAsync()
        {
            await Task.Delay(300, Ct);
            Events.Clear();
        }

        public void Dispose() => _watcher.Dispose();
    }

    [Fact]
    public async Task Reports_working_tree_changes()
    {
        using var dir = new TempDirectory();
        using var recorder = new Recorder();
        recorder.Watcher.Watch(dir.Path);

        dir.WriteFile("src/new.cs", "x");

        var e = await recorder.WaitForEventAsync();
        e.ProjectRoot.Should().Be(ForgeDesk.Core.Common.PathUtil.Normalize(dir.Path));
        e.WorkingTreeChanged.Should().BeTrue();
        e.GitMetadataChanged.Should().BeFalse();
    }

    [Fact]
    public async Task Bursts_are_debounced_into_one_notification()
    {
        using var dir = new TempDirectory();
        using var recorder = new Recorder(TimeSpan.FromMilliseconds(300));
        recorder.Watcher.Watch(dir.Path);

        for (var i = 0; i < 20; i++)
        {
            dir.WriteFile($"file{i}.txt", "x");
        }

        await recorder.WaitForEventAsync();
        await Task.Delay(600, Ct);
        recorder.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task Git_metadata_changes_are_flagged_and_object_writes_ignored()
    {
        using var repo = TestRepository.Create();
        using var recorder = new Recorder();
        recorder.Watcher.Watch(repo.Path);
        await recorder.SettleAsync();

        File.WriteAllText(repo.Combine(".git", "objects", "noise-object"), "x");
        await Task.Delay(500, Ct);
        recorder.Events.Should().BeEmpty("object store writes do not change what the user sees");

        repo.Git("branch", "feature");

        var e = await recorder.WaitForEventAsync(x => x.GitMetadataChanged);
        e.WorkingTreeChanged.Should().BeFalse();
    }

    [Fact]
    public async Task Changes_inside_heavy_folders_are_ignored()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(dir.Combine("node_modules", "lib"));
        using var recorder = new Recorder();
        recorder.Watcher.Watch(dir.Path);
        await recorder.SettleAsync();

        dir.WriteFile("node_modules/lib/index.js", "x");
        await Task.Delay(500, Ct);
        recorder.Events.Should().BeEmpty();

        dir.WriteFile("index.js", "x");
        (await recorder.WaitForEventAsync()).WorkingTreeChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Unwatch_stops_notifications()
    {
        using var dir = new TempDirectory();
        using var recorder = new Recorder();
        recorder.Watcher.Watch(dir.Path);
        recorder.Watcher.Watch(dir.Path);
        recorder.Watcher.WatchedRoots.Should().ContainSingle();

        recorder.Watcher.Unwatch(dir.Path + Path.DirectorySeparatorChar);
        recorder.Watcher.WatchedRoots.Should().BeEmpty();

        dir.WriteFile("after.txt", "x");
        await Task.Delay(500, Ct);
        recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public void Watching_a_missing_folder_is_a_no_op()
    {
        using var dir = new TempDirectory();
        using var recorder = new Recorder();

        recorder.Watcher.Watch(dir.Combine("missing"));
        recorder.Watcher.Unwatch(dir.Combine("missing"));

        recorder.Watcher.WatchedRoots.Should().BeEmpty();
    }

    [Fact]
    public async Task Watches_several_projects_independently()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        using var recorder = new Recorder();
        recorder.Watcher.Watch(first.Path);
        recorder.Watcher.Watch(second.Path);

        second.WriteFile("b.txt", "x");

        var e = await recorder.WaitForEventAsync();
        e.ProjectRoot.Should().Be(ForgeDesk.Core.Common.PathUtil.Normalize(second.Path));
    }

    private static void RaiseError(FileSystemWatcher watcher, Exception error) =>
        typeof(FileSystemWatcher)
            .GetMethod("OnError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(watcher, [new ErrorEventArgs(error)]);

    [Fact]
    public async Task A_watch_failing_while_it_starts_does_not_recurse_or_leak_watchers()
    {
        using var dir = new TempDirectory();
        var starts = 0;
        using var watcher = new ProjectWatcher(TimeSpan.FromMilliseconds(100))
        {
            RecreateDelay = TimeSpan.FromMilliseconds(50),
            MaxConsecutiveFailures = 3,
            StartWatching = w =>
            {
                // Safety net so the unfixed code cannot overflow the test host's stack.
                if (Interlocked.Increment(ref starts) <= 50)
                {
                    RaiseError(w, new IOException("The network share does not support change notifications."));
                }
            },
        };

        watcher.Watch(dir.Path);
        await Task.Delay(1000, Ct);

        starts.Should().BeLessThanOrEqualTo(4, "a failing start must neither recurse nor retry without bound");
        watcher.WatchedRoots.Should().BeEmpty("a folder that cannot be watched is not registered");
    }

    [Fact]
    public async Task A_watch_that_keeps_failing_backs_off_then_gives_up()
    {
        using var dir = new TempDirectory();
        var starts = 0;
        var changes = 0;
        using var watcher = new ProjectWatcher(TimeSpan.FromMilliseconds(20))
        {
            RecreateDelay = TimeSpan.FromMilliseconds(50),
            MaxConsecutiveFailures = 3,
            StartWatching = w =>
            {
                Interlocked.Increment(ref starts);
                w.EnableRaisingEvents = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10);
                    RaiseError(w, new IOException("The specified network name is no longer available."));
                });
            },
        };
        watcher.Changed += (_, _) => Interlocked.Increment(ref changes);

        watcher.Watch(dir.Path);
        await Task.Delay(2000, Ct);
        var startsAfterSettling = Volatile.Read(ref starts);
        var changesAfterSettling = Volatile.Read(ref changes);
        await Task.Delay(1000, Ct);

        startsAfterSettling.Should().Be(4, "the first watch plus three retries");
        Volatile.Read(ref starts).Should().Be(startsAfterSettling, "the watcher gave up");
        Volatile.Read(ref changes).Should().Be(changesAfterSettling, "no refresh is forced once the watcher gave up");
    }

    [Fact]
    public async Task A_buffer_overflow_refreshes_everything_and_keeps_the_same_watcher()
    {
        using var dir = new TempDirectory();
        var created = new ConcurrentQueue<FileSystemWatcher>();
        using var watcher = new ProjectWatcher(TimeSpan.FromMilliseconds(50))
        {
            StartWatching = w =>
            {
                created.Enqueue(w);
                w.EnableRaisingEvents = true;
            },
        };
        var events = new ConcurrentQueue<ProjectFilesChangedEventArgs>();
        watcher.Changed += (_, e) => events.Enqueue(e);
        watcher.Watch(dir.Path);

        RaiseError(created.Single(), new InternalBufferOverflowException());
        await Task.Delay(500, Ct);

        events.Should().ContainSingle(e => e.GitMetadataChanged && e.WorkingTreeChanged);
        created.Should().ContainSingle();
        dir.WriteFile("after.txt", "x");
        await Task.Delay(500, Ct);
        events.Should().Contain(e => !e.GitMetadataChanged && e.WorkingTreeChanged);
    }

    [Fact]
    public void Dispose_is_idempotent_and_blocks_new_watches()
    {
        using var dir = new TempDirectory();
        var watcher = new ProjectWatcher(TimeSpan.FromMilliseconds(100));
        watcher.Watch(dir.Path);

        watcher.Dispose();
        watcher.Dispose();

        var act = () => watcher.Watch(dir.Path);
        act.Should().Throw<ObjectDisposedException>();
    }
}
