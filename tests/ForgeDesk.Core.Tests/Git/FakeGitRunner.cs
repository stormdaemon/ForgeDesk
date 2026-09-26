using ForgeDesk.Core.Processes;

namespace ForgeDesk.Core.Tests.Git;

/// <summary>
/// An <see cref="IProcessRunner"/> standing in for git: records every spec, answers "--version" and
/// delegates other commands to <see cref="Respond"/>, streaming stderr lines like the real runner.
/// </summary>
internal sealed class FakeGitRunner : IProcessRunner
{
    private readonly List<ProcessSpec> _specs = [];

    public string Version { get; set; } = "git version 2.43.0";

    public Func<ProcessSpec, ProcessResult> Respond { get; set; } = _ => Ok();

    public IReadOnlyList<ProcessSpec> Specs
    {
        get
        {
            lock (_specs)
            {
                return _specs.ToList();
            }
        }
    }

    public int VersionProbes => Specs.Count(s => s.Arguments is ["--version"]);

    public static ProcessResult Ok(string stdout = "", string stderr = "") => new(0, stdout, stderr, TimeSpan.FromMilliseconds(1), false);

    /// <summary>The git subcommand and its arguments, without the leading "-c key=value" pairs.</summary>
    public static IReadOnlyList<string> Command(ProcessSpec spec)
    {
        var i = 0;
        while (i + 1 < spec.Arguments.Count && spec.Arguments[i] == "-c")
        {
            i += 2;
        }

        return spec.Arguments.Skip(i).ToList();
    }

    public ProcessSpec Single(string subcommand) => Specs.Single(s => Command(s).FirstOrDefault() == subcommand);

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) =>
        RunAsync(spec, static _ => { }, cancellationToken);

    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputLine> onOutput, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_specs)
        {
            _specs.Add(spec);
        }

        var result = spec.Arguments is ["--version"] ? Ok(Version + "\n") : Respond(spec);
        foreach (var line in result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            onOutput(new OutputLine(OutputStream.StandardError, line));
        }

        return Task.FromResult(result);
    }
}
