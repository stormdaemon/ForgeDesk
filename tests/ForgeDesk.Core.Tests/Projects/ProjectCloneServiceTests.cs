using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Tests.Detection;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Projects;

public sealed class ProjectCloneServiceTests : IAsyncLifetime
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IActivityLog _activity = Substitute.For<IActivityLog>();
    private readonly TempDirectory _workspace = new("clones");
    private TestRepository _origin = null!;
    private TestDatabase _db = null!;
    private ProjectRegistry _registry = null!;
    private ProjectCloneService _service = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _origin = TestRepository.CreateBare();
        using (var source = TestRepository.Create())
        {
            source.Commit("Add app", ("src/app.py", "print('hi')\n"));
            source.Git("remote", "add", "origin", _origin.Path);
            source.Git("push", "origin", "main");
        }

        // The real git service is another domain: clone with the git CLI like it does.
        _git.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Run(() => GitClone(call.ArgAt<string>(0), call.ArgAt<string>(1)), call.ArgAt<CancellationToken>(3)));
        _git.GetRemotesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new GitRemote("origin", "https://github.com/acme/tool.git", null)]);

        _registry = new ProjectRegistry(_db.Database, _git, Substitute.For<IActivityLog>(), Substitute.For<IServiceProvider>(), _clock);
        _service = new ProjectCloneService(_git, _registry, _activity, _clock);
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        _origin.Dispose();
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Clone_creates_the_folder_and_registers_the_project()
    {
        var target = _workspace.Combine("code", "tool");
        var progress = new Progress<GitProgress>();

        var project = await _service.CloneAsync(_origin.Path, target, progress, Ct);

        project.Path.Should().Be(PathUtil.Normalize(target));
        project.Name.Should().Be("tool");
        project.GitHub.Should().Be(new GitHubRepoRef("acme", "tool"));
        File.Exists(Path.Combine(target, "src", "app.py")).Should().BeTrue();
        (await _registry.FindByPathAsync(target, Ct))!.Id.Should().Be(project.Id);
        await _git.Received(1).CloneAsync(_origin.Path, PathUtil.Normalize(target), progress, Arg.Any<CancellationToken>());
        await _activity.Received(1).RecordAsync(
            Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.ProjectCloned && e.ProjectId == project.Id && e.Title == "Cloned acme/tool" && e.Detail == _origin.Path),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clone_into_an_existing_empty_folder()
    {
        var target = _workspace.Combine("empty");
        Directory.CreateDirectory(target);

        var project = await _service.CloneAsync(_origin.Path, target, cancellationToken: Ct);

        project.Path.Should().Be(PathUtil.Normalize(target));
        Directory.Exists(Path.Combine(target, ".git")).Should().BeTrue();
    }

    [Fact]
    public async Task Non_empty_target_is_rejected_without_touching_it()
    {
        var existing = _workspace.WriteFile("busy/notes.txt", "keep me");

        var act = () => _service.CloneAsync(_origin.Path, Path.GetDirectoryName(existing)!, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
        File.ReadAllText(existing).Should().Be("keep me");
        await _git.DidNotReceiveWithAnyArgs().CloneAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_file_at_the_target_path_is_rejected()
    {
        var file = _workspace.WriteFile("taken", "file");

        var act = () => _service.CloneAsync(_origin.Path, file, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
    }

    [Fact]
    public async Task Target_already_used_by_a_project_is_rejected()
    {
        var target = _workspace.Combine("registered");
        Directory.CreateDirectory(target);
        await _registry.AddAsync(target, cancellationToken: Ct);

        var act = () => _service.CloneAsync(_origin.Path, target, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
    }

    [Fact]
    public async Task Failed_clone_removes_what_it_created()
    {
        _git.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var target = call.ArgAt<string>(1);
                Directory.CreateDirectory(Path.Combine(target, ".git", "objects", "pack"));
                var pack = Path.Combine(target, ".git", "objects", "pack", "pack-1.pack");
                File.WriteAllText(pack, "partial");
                File.SetAttributes(pack, FileAttributes.ReadOnly);
                return Task.FromException(new ForgeException(ErrorKind.NetworkUnavailable, "Could not reach github.com."));
            });
        var created = _workspace.Combine("parent", "new-clone");
        var emptied = _workspace.Combine("was-empty");
        Directory.CreateDirectory(emptied);

        var intoNew = () => _service.CloneAsync("https://github.com/acme/tool.git", created, cancellationToken: Ct);
        var intoEmpty = () => _service.CloneAsync("https://github.com/acme/tool.git", emptied, cancellationToken: Ct);

        (await intoNew.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NetworkUnavailable);
        (await intoEmpty.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NetworkUnavailable);
        Directory.Exists(created).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(created)).Should().BeTrue();
        Directory.Exists(emptied).Should().BeTrue("the folder existed before the clone");
        Directory.EnumerateFileSystemEntries(emptied).Should().BeEmpty();
        (await _registry.GetAllAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelled_clone_is_cleaned_up()
    {
        using var cts = new CancellationTokenSource();
        _git.CloneAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<GitProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Directory.CreateDirectory(Path.Combine(call.ArgAt<string>(1), ".git"));
                cts.Cancel();
                return Task.FromCanceled(cts.Token);
            });
        var target = _workspace.Combine("cancelled");

        var act = () => _service.CloneAsync("git@github.com:acme/tool.git", target, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Registration_failure_keeps_the_cloned_files_and_explains()
    {
        var registry = Substitute.For<IProjectRegistry>();
        registry.FindByPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Project?)null);
        registry.AddAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.StorageFailure, "Could not access ForgeDesk's local data.", detail: "disk I/O error"));
        var service = new ProjectCloneService(_git, registry, _activity, _clock);
        var target = _workspace.Combine("kept");

        var act = () => service.CloneAsync(_origin.Path, target, cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.StorageFailure);
        error.Message.Should().Contain("was cloned to").And.Contain(PathUtil.Normalize(target));
        error.Hint.Should().Contain("Add project");
        error.Detail.Should().Be("disk I/O error");
        File.Exists(Path.Combine(target, "src", "app.py")).Should().BeTrue();
        await _activity.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData("ext::sh -c touch% /tmp/pwned")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/acme/tool.git && calc")]
    [InlineData("not a url")]
    [InlineData("C:repo")]
    [InlineData("ftp://example.com/repo.git")]
    public async Task Invalid_addresses_are_rejected_before_cloning(string url)
    {
        var act = () => _service.CloneAsync(url, _workspace.Combine("never"), cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        await _git.DidNotReceiveWithAnyArgs().CloneAsync(default!, default!, default, default);
        Directory.Exists(_workspace.Combine("never")).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://github.com/acme/tool.git")]
    [InlineData("https://github.com/acme/tool")]
    [InlineData("http://git.internal/team/app.git")]
    [InlineData("git@github.com:acme/tool.git")]
    [InlineData("github.com:acme/tool")]
    [InlineData("ssh://git@example.com:2222/acme/tool.git")]
    [InlineData("git://example.com/acme/tool.git")]
    public void Supported_address_forms_are_accepted(string url) =>
        CloneUrl.Validate(url).Should().Be(url);

    [Fact]
    public void Local_repositories_must_exist()
    {
        CloneUrl.Validate(_origin.Path).Should().Be(_origin.Path);
        CloneUrl.Validate(new Uri(_origin.Path).AbsoluteUri).Should().Be(new Uri(_origin.Path).AbsoluteUri);

        var missing = () => CloneUrl.Validate(_workspace.Combine("no-such-repo"));
        missing.Should().Throw<ForgeException>().Which.Kind.Should().Be(ErrorKind.PathNotFound);
    }

    [Theory]
    [InlineData("https://user:ghp_secret@github.com/acme/tool.git", "https://github.com/acme/tool.git")]
    [InlineData("https://github.com/acme/tool.git", "https://github.com/acme/tool.git")]
    [InlineData("git@github.com:acme/tool.git", "git@github.com:acme/tool.git")]
    public void Credentials_are_redacted(string url, string expected) =>
        CloneUrl.Redact(url).Should().Be(expected);

    [Theory]
    [InlineData("https://github.com/acme/My.Tool.git", "My.Tool")]
    [InlineData("git@github.com:acme/tool", "tool")]
    [InlineData("https://github.com/acme/tool/", "tool")]
    public void Suggested_folder_name_follows_git(string url, string expected) =>
        CloneUrl.SuggestedFolderName(url).Should().Be(expected);

    private static void GitClone(string url, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "clone", "--quiet", url, target })
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        stdout.Wait();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new ForgeException(ErrorKind.GitCommandFailed, "git clone failed.", detail: stderr);
        }
    }
}
