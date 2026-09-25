using ForgeDesk.Core.Common;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.Core.Tests.GitHub.Fakes;

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public FakeClock()
        : this(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset Now { get; private set; } = start;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Records every process spec and answers with a scripted result.</summary>
public sealed class FakeProcessRunner(Func<ProcessSpec, ProcessResult> respond) : IProcessRunner
{
    private readonly Lock _gate = new();
    private readonly List<ProcessSpec> _calls = [];

    public IReadOnlyList<ProcessSpec> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    public static ProcessResult Success(string stdout) => new(0, stdout, string.Empty, TimeSpan.FromMilliseconds(5), false);

    public static ProcessResult Failure(int exitCode, string stderr) => new(exitCode, string.Empty, stderr, TimeSpan.FromMilliseconds(5), false);

    public static ProcessResult TimedOut() => new(-1, string.Empty, string.Empty, TimeSpan.FromMinutes(5), true);

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _calls.Add(spec);
        }

        return Task.FromResult(respond(spec));
    }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputLine> onOutput, CancellationToken cancellationToken = default) =>
        RunAsync(spec, cancellationToken);
}

public sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = AppSettings.Default;

    public event EventHandler<AppSettings>? Changed;

    public int Writes { get; private set; }

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        Writes++;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default) =>
        SaveAsync(change(Current), cancellationToken);
}

internal sealed class FakeGitHubCliLocator(string? path) : IGitHubCliLocator
{
    public string? Path { get; set; } = path;

    public string? Find() => Path;
}

/// <summary>Collects progress reports synchronously (Progress&lt;T&gt; would post them asynchronously).</summary>
public sealed class RecordingProgress<T> : IProgress<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _reports = [];

    public IReadOnlyList<T> Reports
    {
        get
        {
            lock (_gate)
            {
                return _reports.ToList();
            }
        }
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            _reports.Add(value);
        }
    }
}
