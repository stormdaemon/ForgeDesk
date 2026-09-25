using System.Net;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Security;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ForgeDesk.Core.Tests.GitHub.Fakes;

/// <summary>A real <see cref="GitHubAccountService"/> over fakes: API, vault, settings, processes, gh location.</summary>
internal sealed class AccountHarness : IDisposable
{
    public const string GitPath = "git-under-test";

    public AccountHarness()
    {
        Git.FindGitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<GitInstallation?>(new GitInstallation(GitPath, "2.46.0.windows.1")));
        Runner = new FakeProcessRunner(spec => Processes(spec));
        Factory = new GitHubClientFactory(new GitHubClientOptions { BaseAddress = FakeGitHubApi.BaseAddress, CreateHandler = () => Api });
        Service = new GitHubAccountService(
            Session,
            Secrets,
            Settings,
            new GitHubTokenValidator(Factory),
            new GitCredentialManagerClient(Git, Runner, new CapturingLogger<GitCredentialManagerClient>(Logs)),
            new GitHubCliClient(CliLocator, Runner),
            new CapturingLogger<GitHubAccountService>(Logs));
        Service.AccountChanged += (_, account) => Events.Add(account);
    }

    public FakeGitHubApi Api { get; } = new();

    public InMemorySecretStore Secrets { get; } = new();

    public FakeSettingsService Settings { get; } = new();

    public GitHubSession Session { get; } = new();

    public IGitService Git { get; } = Substitute.For<IGitService>();

    public FakeGitHubCliLocator CliLocator { get; } = new(Path.Combine("tools", "gh.exe"));

    public Func<ProcessSpec, ProcessResult> Processes { get; set; } = spec => FakeProcessRunner.Failure(1, $"unexpected process: {spec}");

    public FakeProcessRunner Runner { get; }

    public GitHubClientFactory Factory { get; }

    public GitHubAccountService Service { get; }

    public List<GitHubAccount?> Events { get; } = [];

    public List<string> Logs { get; } = [];

    /// <summary>GitHub answers GET /user with the given scopes header (null = no header, like fine-grained tokens).</summary>
    public void ServeUser(string login = "octocat", string? scopes = "repo, workflow, read:org")
    {
        var headers = scopes is null ? System.Array.Empty<(string, string)>() : [("X-OAuth-Scopes", scopes)];
        Api.OnGet("/user", GitHubPayloads.User(login), headers);
    }

    public void RejectTokens() =>
        Api.OnError(HttpMethod.Get, "/user", HttpStatusCode.Unauthorized, """{"message":"Bad credentials","documentation_url":"https://docs.github.com/rest"}""");

    public void GoOffline() =>
        Api.FailAllWith(() => new HttpRequestException("No such host is known. (api.github.test:443)"));

    public async Task StoreSessionAsync(string token, string json)
    {
        await Secrets.SetAsync(GitHubAccountService.TokenSecretKey, token);
        await Secrets.SetAsync(GitHubAccountService.SessionSecretKey, json);
    }

    public void Dispose() => Factory.Dispose();
}

internal sealed class CapturingLogger<T>(List<string> sink) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (sink)
        {
            sink.Add($"{logLevel}: {formatter(state, exception)} {exception}");
        }
    }
}
