using System.Net;
using System.Net.Sockets;
using System.Text;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Git;

/// <summary>
/// A folder the user downloads or unzips brings its own .git/config: ForgeDesk's automatic git
/// commands must not run programs it names, nor send the GitHub token where it says.
/// </summary>
public sealed class GitSecurityTests : IDisposable
{
    private const string Token = "ghp_SuperSecretToken1234567890";

    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private static string Header(string user, string password) =>
        "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    // ---- Repository config must not run programs during automatic reads (SEC-1) ----

    [Fact]
    public async Task Status_does_not_run_the_repository_fsmonitor()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("config", "core.fsmonitor", "echo ran > fsmonitor-ran; false");

        await _sandbox.Git.GetStatusAsync(repo.Path, Ct);

        File.Exists(repo.Combine("fsmonitor-ran")).Should().BeFalse("core.fsmonitor from the repository must never run");
    }

    [Fact]
    public async Task File_listing_does_not_run_the_repository_fsmonitor()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("config", "core.fsmonitor", "echo ran > fsmonitor-ran; false");
        var git = new Core.Files.GitCli(ProcessRunner.Instance);

        await git.TryRunAsync(repo.Path, ["ls-files", "--deleted"], Ct);
        await git.TryRunAsync(repo.Path, ["ls-files", "--modified"], Ct);

        File.Exists(repo.Combine("fsmonitor-ran")).Should().BeFalse();
    }

    [Theory]
    [InlineData("clean", false)]
    [InlineData("process", false)]
    [InlineData("clean", true)]
    public async Task Status_and_diff_do_not_run_repository_filter_drivers(string command, bool throughInclude)
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Filtered", (".gitattributes", "*.txt filter=evil\n"), ("notes.txt", "hello\n"));
        var program = command == "clean" ? "echo ran > filter-ran; cat" : "echo ran > filter-ran";
        if (throughInclude)
        {
            var included = repo.WriteFile(".git/extra.cfg", $"[filter \"evil\"]\n\t{command} = {program}\n");
            repo.Git("config", "include.path", included);
        }
        else
        {
            repo.Git("config", $"filter.evil.{command}", program);
        }

        // An extracted or copied repository: every file's stat data differs from the index.
        File.SetLastWriteTimeUtc(repo.Combine("notes.txt"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Swallow(() => _sandbox.Git.GetStatusAsync(repo.Path, Ct));
        await Swallow(() => _sandbox.Git.GetFileDiffAsync(repo.Path, "notes.txt", DiffTarget.WorkingTree, Ct));

        File.Exists(repo.Combine("filter-ran")).Should().BeFalse($"filter.<driver>.{command} from the repository must never run on a read");
    }

    [Fact]
    public async Task Filter_drivers_from_the_user_configuration_still_run()
    {
        var marker = Path.Combine(_sandbox.NewDirectory("marker"), "global-filter-ran").Replace('\\', '/');
        File.AppendAllText(_sandbox.GlobalConfigPath, $"[filter \"mine\"]\n\tclean = echo ran > '{marker}'; cat\n");
        var repo = _sandbox.CreateRepository();
        _sandbox.Commit(repo.Path, "Filtered", null, (".gitattributes", "*.dat filter=mine\n"), ("data.dat", "hello\n"));
        File.Delete(marker);
        File.SetLastWriteTimeUtc(repo.Combine("data.dat"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var status = await _sandbox.Git.GetStatusAsync(repo.Path, Ct);

        status.Entries.Should().BeEmpty();
        File.Exists(marker).Should().BeTrue("Git LFS and other drivers the user installed must keep working");
    }

    // ---- Token must not go to a repository that redirects or weakens TLS (SEC-2) ----

    [Theory]
    [InlineData("http.https://github.com/.sslVerify", "false")]
    [InlineData("http.sslVerify", "false")]
    [InlineData("http.sslCAInfo", "ca.pem")]
    [InlineData("http.https://github.com/octo/app.git.sslCAPath", "certs")]
    [InlineData("http.proxy", "http://127.0.0.1:9")]
    [InlineData("remote.origin.proxy", "http://127.0.0.1:9")]
    [InlineData("http.curloptResolve", "github.com:443:127.0.0.1")]
    [InlineData("http.sslBackend", "schannel")]
    public async Task Token_is_withheld_when_the_repository_changes_https_settings(string key, string value)
    {
        var (repo, runner) = RepositoryWithGitHubOrigin();
        repo.Git("config", key, value);

        await CreateService(runner).FetchAsync(repo.Path, cancellationToken: Ct);

        runner.Fetch.Environment.Values.Should().NotContain(v => v != null && v.Contains(Header("x-access-token", Token), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Token_is_withheld_when_an_included_file_changes_https_settings()
    {
        var (repo, runner) = RepositoryWithGitHubOrigin();
        var included = repo.WriteFile(".git/extra.cfg", "[http]\n\tsslVerify = false\n");
        repo.Git("config", "include.path", included);

        await CreateService(runner).FetchAsync(repo.Path, cancellationToken: Ct);

        runner.Fetch.Environment.Values.Should().NotContain(v => v != null && v.StartsWith("AUTHORIZATION", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Token_is_passed_when_the_repository_leaves_https_alone()
    {
        var (repo, runner) = RepositoryWithGitHubOrigin();
        repo.Git("config", "http.postBuffer", "524288000");

        await CreateService(runner).FetchAsync(repo.Path, cancellationToken: Ct);

        runner.Fetch.Environment.Values.Should().Contain(Header("x-access-token", Token));
    }

    // ---- Hook output must not leak the token into errors (SEC-3) ----

    [Fact]
    public async Task Token_printed_by_a_hook_is_masked_in_errors()
    {
        var runner = new FakeGitRunner();
        var fakeGit = _sandbox.NewDirectory("bin");
        var executable = Path.Combine(fakeGit, "git.exe");
        File.WriteAllText(executable, string.Empty);
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(AppSettings.Default with { GitExecutablePath = executable });
        var header = Header("x-access-token", Token);
        runner.Respond = spec => FakeGitRunner.Command(spec) switch
        {
            ["remote", "-v", ..] => FakeGitRunner.Ok("origin\thttps://github.com/octo/app.git (fetch)\norigin\thttps://github.com/octo/app.git (push)\n"),
            ["fetch", ..] => new ProcessResult(1, string.Empty,
                $"GIT_CONFIG_VALUE_1={header}\nGITHUB_TOKEN={Token}\nerror: hook declined to update refs\n", TimeSpan.Zero, false),
            _ => FakeGitRunner.Ok(),
        };
        var service = new GitService(settings, runner, [GitHubProvider()], new Dictionary<string, string?>());

        var act = () => service.FetchAsync(fakeGit, cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        var base64 = header["AUTHORIZATION: basic ".Length..];
        foreach (var text in new[] { error.Message, error.Detail ?? string.Empty, error.Hint ?? string.Empty })
        {
            text.Should().NotContain(base64).And.NotContain(Token);
        }

        error.Detail.Should().Contain("hook declined");
    }

    [Theory]
    [InlineData("GIT_CONFIG_VALUE_1=AUTHORIZATION: basic eC1hY2Nlc3MtdG9rZW46Z2hw", "GIT_CONFIG_VALUE_1=AUTHORIZATION: basic ***")]
    [InlineData("> Authorization: Bearer abc.def", "> Authorization: Bearer ***")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ leaked", "token *** leaked")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz", "***")]
    [InlineData("Authorization required", "Authorization required")]
    public void Authorization_values_and_tokens_are_masked(string text, string expected) =>
        GitRedaction.Redact(text).Should().Be(expected);

    // ---- Which remotes get the token must not depend on their order (SEC-4) ----

    [Theory]
    [InlineData("backup", "origin")]
    [InlineData("origin", "zbackup")]
    public async Task Token_reaches_exactly_the_remotes_without_their_own_credentials(string first, string second)
    {
        using var server = new RecordingHttpServer();
        var repo = _sandbox.CreateRepository();
        var withOwnCredentials = first.Contains("backup", StringComparison.Ordinal) ? first : second;
        var plain = withOwnCredentials == first ? second : first;
        repo.Git("remote", "add", withOwnCredentials, $"http://bot:botpass@127.0.0.1:{server.Port}/org/mirror.git");
        repo.Git("remote", "add", plain, $"http://127.0.0.1:{server.Port}/me/app.git");
        var provider = new UrlProvider(url => new Uri(url).UserInfo.Contains(':', StringComparison.Ordinal) ? null : new GitCredential("me", Token));
        var environment = new Dictionary<string, string?>(_sandbox.Environment)
        {
            ["http_proxy"] = null,
            ["HTTP_PROXY"] = null,
            ["https_proxy"] = null,
            ["HTTPS_PROXY"] = null,
            ["all_proxy"] = null,
            ["ALL_PROXY"] = null,
            ["no_proxy"] = "*",
            ["NO_PROXY"] = "*",
        };
        var service = new GitService(_sandbox.Settings, ProcessRunner.Instance, [provider], environment);

        // The server answers 404: both remotes fail, after sending their first request.
        await Swallow(() => service.FetchAsync(repo.Path, cancellationToken: Ct));

        var ours = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"me:{Token}"));
        var requests = server.Requests;
        requests.Should().Contain(r => r.Path.StartsWith("/me/app.git/", StringComparison.Ordinal) && string.Equals(r.Authorization, ours, StringComparison.OrdinalIgnoreCase),
            "the signed-in token authenticates the remote without credentials of its own");
        requests.Should().Contain(r => r.Path.StartsWith("/org/mirror.git/", StringComparison.Ordinal));
        requests.Where(r => r.Path.StartsWith("/org/mirror.git/", StringComparison.Ordinal))
            .Should().NotContain(r => string.Equals(r.Authorization, ours, StringComparison.OrdinalIgnoreCase), "a URL with its own credentials keeps them");
    }

    [Fact]
    public async Task One_header_covers_the_host_when_no_remote_has_its_own_credentials()
    {
        var environment = await GitCredentialEnvironment.BuildAsync(
            [new UrlProvider(_ => new GitCredential("me", Token))],
            ["https://github.com/me/app.git", "https://github.com/me/other.git", "git@github.com:me/ssh.git"],
            0, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);

        environment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = string.Empty,
            ["GIT_CONFIG_KEY_1"] = "http.https://github.com/.extraHeader",
            ["GIT_CONFIG_VALUE_1"] = Header("me", Token),
            ["GIT_CONFIG_COUNT"] = "2",
        });
    }

    public void Dispose() => _sandbox.Dispose();

    private (TestRepository Repo, FetchRecordingRunner Runner) RepositoryWithGitHubOrigin()
    {
        var repo = _sandbox.CreateRepository();
        repo.Git("remote", "add", "origin", "https://github.com/octo/app.git");
        return (repo, new FetchRecordingRunner());
    }

    private GitService CreateService(IProcessRunner runner)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(AppSettings.Default);
        return new GitService(settings, runner, [GitHubProvider()], _sandbox.Environment);
    }

    private static IGitCredentialProvider GitHubProvider() =>
        new UrlProvider(url => ForgeDesk.Core.GitHub.GitHubCredentialProvider.IsGitHubHttpsRemote(url) ? new GitCredential("x-access-token", Token) : null);

    private static async Task Swallow(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ForgeException)
        {
            // Only the side effects matter here.
        }
    }

    private sealed class UrlProvider(Func<string, GitCredential?> resolve) : IGitCredentialProvider
    {
        public Task<GitCredential?> GetCredentialAsync(string remoteUrl, CancellationToken cancellationToken = default) => Task.FromResult(resolve(remoteUrl));
    }

    /// <summary>Real git for everything but "fetch", which is recorded and answered without touching the network.</summary>
    private sealed class FetchRecordingRunner : IProcessRunner
    {
        private ProcessSpec? _fetch;

        public ProcessSpec Fetch => _fetch ?? throw new InvalidOperationException("git fetch did not run");

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) =>
            RunAsync(spec, static _ => { }, cancellationToken);

        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputLine> onOutput, CancellationToken cancellationToken = default)
        {
            if (FakeGitRunner.Command(spec).FirstOrDefault() == "fetch")
            {
                _fetch = spec;
                return Task.FromResult(FakeGitRunner.Ok());
            }

            return ProcessRunner.Instance.RunAsync(spec, onOutput, cancellationToken);
        }
    }

    /// <summary>A plain HTTP server on 127.0.0.1 that records each request's path and Authorization header, then answers 404.</summary>
    private sealed class RecordingHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<(string Path, string? Authorization)> _requests = [];
        private readonly CancellationTokenSource _stop = new();

        public RecordingHttpServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<(string Path, string? Authorization)> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync();
                    string? authorization = null;
                    for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                    {
                        if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                        {
                            authorization = line["Authorization:".Length..].Trim();
                        }
                    }

                    var path = requestLine?.Split(' ') is [_, var target, ..] ? target : string.Empty;
                    lock (_requests)
                    {
                        _requests.Add((path, authorization));
                    }

                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response);
                }
                catch (IOException)
                {
                    // Client went away.
                }
            }
        }
    }
}
