using System.Globalization;
using System.Text;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitCliTests : IDisposable
{
    private const string Token = "ghp_SuperSecretToken123";
    private const string RemoteUrl = "https://github.com/octo/app.git";

    private readonly TempDirectory _dir = new("git-cli");
    private readonly FakeGitRunner _runner = new();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    public GitCliTests()
    {
        // The locator only needs a file to exist; the fake runner answers "--version".
        var fakeGit = _dir.WriteFile("bin/git.exe", string.Empty);
        _settings.Current.Returns(AppSettings.Default with { GitExecutablePath = fakeGit });
        _runner.Respond = spec => FakeGitRunner.Command(spec) switch
        {
            ["remote", "-v", ..] => FakeGitRunner.Ok($"origin\t{RemoteUrl} (fetch)\norigin\t{RemoteUrl} (push)\nmirror\tgit@github.com:octo/app.git (fetch)\nmirror\tgit@github.com:octo/app.git (push)\n"),
            _ => FakeGitRunner.Ok(),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int InheritedCount => GitCredentialEnvironment.InheritedConfigCount(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"));

    private GitService CreateService(params IGitCredentialProvider[] providers) =>
        new(_settings, _runner, providers, new Dictionary<string, string?>());

    private static IGitCredentialProvider Provider(GitCredential? credential)
    {
        var provider = Substitute.For<IGitCredentialProvider>();
        provider.GetCredentialAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(credential);
        return provider;
    }

    [Fact]
    public async Task Credentials_are_passed_through_environment_and_never_on_the_command_line()
    {
        var service = CreateService(Provider(new GitCredential("x-access-token", Token)));

        await service.FetchAsync(_dir.Path, cancellationToken: Ct);

        var fetch = _runner.Single("fetch");
        fetch.Arguments.Should().NotContain(a => a.Contains(Token, StringComparison.Ordinal) || a.Contains("extraHeader", StringComparison.OrdinalIgnoreCase));
        fetch.ToString().Should().NotContain(Token);
        var first = InheritedCount;
        var expectedHeader = "AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{Token}"));
        fetch.Environment["GIT_CONFIG_COUNT"].Should().Be((first + 2).ToString(CultureInfo.InvariantCulture));
        fetch.Environment[$"GIT_CONFIG_KEY_{first}"].Should().Be("http.https://github.com/.extraHeader");
        fetch.Environment[$"GIT_CONFIG_VALUE_{first}"].Should().BeEmpty("the first entry resets headers configured elsewhere");
        fetch.Environment[$"GIT_CONFIG_KEY_{first + 1}"].Should().Be("http.https://github.com/.extraHeader");
        fetch.Environment[$"GIT_CONFIG_VALUE_{first + 1}"].Should().Be(expectedHeader);
        FakeGitRunner.Command(fetch).Should().Equal("fetch", "--all", "--prune", "--progress");
    }

    [Fact]
    public async Task Clone_asks_providers_for_the_clone_url()
    {
        var provider = Provider(new GitCredential("me", Token));
        var service = CreateService(provider);
        var target = Path.Combine(_dir.Path, "clones", "app");

        await service.CloneAsync(RemoteUrl, target, cancellationToken: Ct);

        await provider.Received(1).GetCredentialAsync(RemoteUrl, Arg.Any<CancellationToken>());
        var clone = _runner.Single("clone");
        FakeGitRunner.Command(clone).Should().Equal("clone", "--progress", "--", RemoteUrl, PathUtil.Normalize(target));
        clone.Arguments.Should().NotContain(a => a.Contains(Token, StringComparison.Ordinal));
        clone.Environment.Values.Should().Contain(v => v != null && v.StartsWith("AUTHORIZATION: basic ", StringComparison.Ordinal));
        clone.Timeout.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task No_credential_means_no_git_config_variables()
    {
        var service = CreateService(Provider(null));

        await service.FetchAsync(_dir.Path, cancellationToken: Ct);

        _runner.Single("fetch").Environment.Keys.Should().NotContain(k => k.StartsWith("GIT_CONFIG_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_provider_does_not_block_git()
    {
        var broken = Substitute.For<IGitCredentialProvider>();
        broken.GetCredentialAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("vault locked"));
        var service = CreateService(broken, Provider(new GitCredential("me", Token)));

        await service.FetchAsync(_dir.Path, cancellationToken: Ct);

        _runner.Single("fetch").Environment.Values.Should().Contain(v => v != null && v.StartsWith("AUTHORIZATION", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ssh_remotes_are_never_offered_to_providers()
    {
        var provider = Provider(new GitCredential("me", Token));
        var service = CreateService(provider);

        await service.FetchAsync(_dir.Path, cancellationToken: Ct);

        await provider.DidNotReceive().GetCredentialAsync(Arg.Is<string>(u => u.StartsWith("git@", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Providers_are_skipped_when_the_setting_is_off()
    {
        var withoutToken = _settings.Current with { UseGitHubTokenForGit = false };
        _settings.Current.Returns(withoutToken);
        var provider = Provider(new GitCredential("me", Token));

        await CreateService(provider).FetchAsync(_dir.Path, cancellationToken: Ct);

        await provider.DidNotReceiveWithAnyArgs().GetCredentialAsync(default!, default);
    }

    [Fact]
    public async Task Fetch_reports_parsed_progress_once_per_step()
    {
        _runner.Respond = spec => FakeGitRunner.Command(spec)[0] == "remote"
            ? FakeGitRunner.Ok($"origin\t{RemoteUrl} (fetch)\norigin\t{RemoteUrl} (push)\n")
            : FakeGitRunner.Ok(stderr: "remote: Counting objects: 100% (3/3), done.\nReceiving objects:  50% (1/2)\nReceiving objects:  50% (1/2)\nReceiving objects: 100% (2/2), done.\nFrom https://github.com/octo/app\n");
        var reports = new List<GitProgress>();

        await CreateService().FetchAsync(_dir.Path, new InlineProgress(reports.Add), Ct);

        reports.Select(r => (r.Stage, r.Percent)).Should().Equal(("Counting objects", 100), ("Receiving objects", 50), ("Receiving objects", 100));
    }

    [Fact]
    public async Task Fetch_without_remotes_does_nothing()
    {
        _runner.Respond = _ => FakeGitRunner.Ok();

        await CreateService().FetchAsync(_dir.Path, cancellationToken: Ct);

        _runner.Specs.Should().NotContain(s => FakeGitRunner.Command(s).FirstOrDefault() == "fetch");
    }

    [Fact]
    public void Spec_uses_an_argument_list_and_a_predictable_environment()
    {
        var read = GitCli.BuildSpec("git", new GitRequest { WorkingDirectory = _dir.Path, Arguments = ["status", "--porcelain=v2"] }, new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = "cfg" });
        var write = GitCli.BuildSpec("git", new GitRequest { WorkingDirectory = _dir.Path, Arguments = ["commit", "--file=-"], Kind = GitCommandKind.Write, StandardInput = "msg\n" }, new Dictionary<string, string?>());

        read.Arguments.Should().Equal("-c", "core.quotepath=false", "-c", "color.ui=false", "status", "--porcelain=v2");
        read.RawArguments.Should().BeNull();
        read.WorkingDirectory.Should().Be(_dir.Path);
        read.Environment.Should().Contain(new KeyValuePair<string, string?>("GIT_TERMINAL_PROMPT", "0"))
            .And.Contain(new KeyValuePair<string, string?>("LC_ALL", "C"))
            .And.Contain(new KeyValuePair<string, string?>("LANG", "C"))
            .And.Contain(new KeyValuePair<string, string?>("GIT_OPTIONAL_LOCKS", "0"))
            .And.Contain(new KeyValuePair<string, string?>("GIT_CONFIG_GLOBAL", "cfg"));
        read.Timeout.Should().Be(TimeSpan.FromSeconds(60));
        write.Environment.Should().NotContainKey("GIT_OPTIONAL_LOCKS");
        write.StandardInput.Should().Be("msg\n");
        write.Timeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Request_config_is_added_before_the_subcommand()
    {
        var spec = GitCli.BuildSpec("git", new GitRequest { WorkingDirectory = ".", Arguments = ["log"], Config = ["log.showSignature=false"], Kind = GitCommandKind.Network }, new Dictionary<string, string?>());

        spec.Arguments.Should().Equal("-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "log.showSignature=false", "log");
        spec.Timeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData("https://github.com/octo/app.git", "https://github.com/")]
    [InlineData("https://x-access-token:abc@github.com:443/octo/app", "https://github.com/")]
    [InlineData("http://git.local:8080/team/app.git", "http://git.local:8080/")]
    [InlineData("HTTPS://GitHub.com/Octo/App.git", "https://github.com/")]
    [InlineData("git@github.com:octo/app.git", null)]
    [InlineData("ssh://git@github.com/octo/app.git", null)]
    [InlineData("C:\\src\\app", null)]
    [InlineData("", null)]
    public void Http_scope_of_remote_urls(string url, string? expected) =>
        GitCredentialEnvironment.HttpScope(url).Should().Be(expected);

    [Fact]
    public void Config_entries_are_appended_after_inherited_ones()
    {
        var environment = GitCredentialEnvironment.ToEnvironment([new("a.b", "1"), new("c.d", "2")], inheritedConfigCount: 3);

        environment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["GIT_CONFIG_KEY_3"] = "a.b",
            ["GIT_CONFIG_VALUE_3"] = "1",
            ["GIT_CONFIG_KEY_4"] = "c.d",
            ["GIT_CONFIG_VALUE_4"] = "2",
            ["GIT_CONFIG_COUNT"] = "5",
        });
        GitCredentialEnvironment.InheritedConfigCount("abc").Should().Be(0);
        GitCredentialEnvironment.InheritedConfigCount(null).Should().Be(0);
    }

    public void Dispose() => _dir.Dispose();

    private sealed class InlineProgress(Action<GitProgress> report) : IProgress<GitProgress>
    {
        public void Report(GitProgress value) => report(value);
    }
}

public sealed class GitLocatorTests : IDisposable
{
    private readonly TempDirectory _dir = new("git-locator");
    private readonly FakeGitRunner _runner = new();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GitLocator CreateLocator(string? configuredPath)
    {
        _settings.Current.Returns(AppSettings.Default with { GitExecutablePath = configuredPath });
        return new GitLocator(_settings, _runner, new Dictionary<string, string?>());
    }

    [Fact]
    public async Task Configured_path_that_does_not_exist_means_git_is_not_found()
    {
        using var sandbox = new GitSandbox(AppSettings.Default with { GitExecutablePath = _dir.Combine("missing", "git.exe") });

        (await sandbox.Git.FindGitAsync(Ct)).Should().BeNull();
        var act = () => sandbox.Git.GetStatusAsync(_dir.Path, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.GitNotFound);
        error.Hint.Should().Contain("Settings");
    }

    [Fact]
    public async Task Configured_install_folder_is_searched_for_the_executable()
    {
        var executable = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var expected = _dir.WriteFile($"Git/cmd/{executable}", string.Empty);
        var locator = CreateLocator(_dir.Combine("Git"));

        var installation = await locator.FindAsync(Ct);

        installation.Should().Be(new GitInstallation(expected, "2.43.0"));
    }

    [Fact]
    public async Task Real_git_is_found_on_the_path()
    {
        using var sandbox = new GitSandbox();

        var installation = await sandbox.Git.FindGitAsync(Ct);

        installation.Should().NotBeNull();
        File.Exists(installation!.ExecutablePath).Should().BeTrue();
        Version.Parse(installation.Version).Should().BeGreaterThanOrEqualTo(GitLocator.MinimumVersion);
    }

    [Fact]
    public async Task Successful_lookups_are_cached_until_the_setting_changes()
    {
        var first = _dir.WriteFile("a/git.exe", string.Empty);
        var second = _dir.WriteFile("b/git.exe", string.Empty);
        var locator = CreateLocator(first);

        (await locator.FindAsync(Ct))!.ExecutablePath.Should().Be(first);
        await locator.FindAsync(Ct);
        _runner.VersionProbes.Should().Be(1);

        var changed = AppSettings.Default with { GitExecutablePath = second };
        _settings.Current.Returns(changed);
        _settings.Changed += Raise.Event<EventHandler<AppSettings>>(_settings, changed);

        (await locator.FindAsync(Ct))!.ExecutablePath.Should().Be(second);
        _runner.VersionProbes.Should().Be(2);
    }

    [Fact]
    public async Task Missing_git_is_not_cached_so_installing_it_later_works()
    {
        var path = _dir.Combine("later", "git.exe");
        var locator = CreateLocator(path);

        (await locator.FindAsync(Ct)).Should().BeNull();
        _dir.WriteFile("later/git.exe", string.Empty);

        (await locator.FindAsync(Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Too_old_git_is_refused_with_guidance()
    {
        _runner.Version = "git version 2.20.1.windows.1";
        var locator = CreateLocator(_dir.WriteFile("old/git.exe", string.Empty));

        var act = () => locator.RequireAsync(Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.GitNotFound);
        error.Message.Should().Contain("2.20.1").And.Contain("too old");
        error.Hint.Should().Contain("https://git-scm.com/download/win");
    }

    [Fact]
    public async Task A_program_that_is_not_git_is_ignored()
    {
        _runner.Version = "Python 3.12.0";
        var locator = CreateLocator(_dir.WriteFile("fake/git.exe", string.Empty));

        (await locator.FindAsync(Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData("git version 2.43.0\n", "2.43.0")]
    [InlineData("git version 2.45.1.windows.1", "2.45.1")]
    [InlineData("git version 2.39.3 (Apple Git-145)", "2.39.3")]
    [InlineData("git version 2.40", "2.40")]
    [InlineData("something else", null)]
    public void Parses_the_version_banner(string output, string? expected) =>
        GitLocator.ParseVersion(output).Should().Be(expected);

    [Fact]
    public void Well_known_windows_locations_follow_the_environment()
    {
        var variables = new Dictionary<string, string?>
        {
            ["ProgramFiles"] = @"C:\Program Files",
            ["ProgramW6432"] = @"C:\Program Files",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["LOCALAPPDATA"] = @"C:\Users\ada\AppData\Local",
        };

        var locations = GitLocator.WellKnownWindowsLocations(name => variables.GetValueOrDefault(name));

        locations.Should().Equal(
            Path.Combine(@"C:\Program Files", "Git", "cmd", "git.exe"),
            Path.Combine(@"C:\Program Files (x86)", "Git", "cmd", "git.exe"),
            Path.Combine(@"C:\Users\ada\AppData\Local", "Programs", "Git", "cmd", "git.exe"));
    }

    public void Dispose() => _dir.Dispose();
}
