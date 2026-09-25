using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public sealed class GitServiceRepositoryTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private static CancellationToken Ct => GitSandbox.Token;

    private IGitService Git => _sandbox.Git;

    [Fact]
    public async Task Repository_root_is_found_from_a_subfolder_or_a_file()
    {
        var repo = _sandbox.CreateRepository();
        var file = repo.WriteFile("src/deep/code.cs", "class C {}\n");

        var fromFolder = await Git.GetRepositoryRootAsync(repo.Combine("src", "deep"), Ct);
        var fromFile = await Git.GetRepositoryRootAsync(file, Ct);

        fromFolder.Should().Be(PathUtil.Normalize(repo.Path));
        fromFile.Should().Be(PathUtil.Normalize(repo.Path));
        (await Git.IsRepositoryAsync(repo.Path, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Plain_folders_git_directories_and_missing_paths_are_not_repositories()
    {
        var repo = _sandbox.CreateRepository();
        var plain = _sandbox.NewDirectory("plain");

        (await Git.IsRepositoryAsync(plain, Ct)).Should().BeFalse();
        (await Git.GetRepositoryRootAsync(Path.Combine(repo.Path, ".git"), Ct)).Should().BeNull();
        (await Git.GetRepositoryRootAsync(Path.Combine(plain, "does-not-exist"), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Operations_outside_a_repository_report_not_a_repository()
    {
        var plain = _sandbox.NewDirectory("plain");

        var act = () => Git.GetStatusAsync(plain, Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.NotARepository);
        error.Detail.Should().Contain("git status");
    }

    [Fact]
    public async Task Init_creates_a_repository_on_main()
    {
        var target = Path.Combine(_sandbox.NewDirectory("init"), "new project");

        await Git.InitAsync(target, Ct);

        (await Git.IsRepositoryAsync(target, Ct)).Should().BeTrue();
        var status = await Git.GetStatusAsync(target, Ct);
        status.IsUnborn.Should().BeTrue();
        status.Branch.Should().Be("main");
    }

    [Fact]
    public async Task Clone_copies_the_repository_and_reports_progress()
    {
        var source = _sandbox.CreateRepository();
        source.Commit("Second", ("src/a.txt", "a\n"));
        var bare = _sandbox.CreateRemoteFor(source);
        var target = Path.Combine(_sandbox.NewDirectory("clone"), "app");
        var reports = new List<GitProgress>();

        // A file:// URL goes through the pack protocol (a plain path would just hard-link), like a real remote.
        await Git.CloneAsync(new Uri(bare.Path).AbsoluteUri, target, new SyncProgress<GitProgress>(reports.Add), Ct);

        File.ReadAllText(Path.Combine(target, "src", "a.txt")).Should().Be("a\n");
        var status = await Git.GetStatusAsync(target, Ct);
        status.Branch.Should().Be("main");
        status.Upstream.Should().Be("origin/main");
        reports.Should().Contain(r => r.Stage == "Receiving objects" && r.Percent == 100);
        reports.Should().Contain(r => r.Stage == "Counting objects");
    }

    [Fact]
    public async Task Clone_refuses_a_non_empty_folder()
    {
        var source = _sandbox.CreateRepository();
        var target = _sandbox.NewDirectory("busy");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "mine");

        var act = () => Git.CloneAsync(source.Path, target, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
        File.ReadAllText(Path.Combine(target, "keep.txt")).Should().Be("mine");
    }

    [Fact]
    public async Task Failed_clone_leaves_nothing_behind()
    {
        var parent = _sandbox.NewDirectory("clone");
        var target = Path.Combine(parent, "app");
        var emptyTarget = _sandbox.NewDirectory("empty-target");
        var missing = Path.Combine(parent, "no-such-repository");

        var intoNew = () => Git.CloneAsync(missing, target, cancellationToken: Ct);
        var intoEmpty = () => Git.CloneAsync(missing, emptyTarget, cancellationToken: Ct);

        (await intoNew.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
        await intoEmpty.Should().ThrowAsync<ForgeException>();
        Directory.Exists(target).Should().BeFalse();
        Directory.Exists(emptyTarget).Should().BeTrue("a folder the user picked is kept");
        Directory.EnumerateFileSystemEntries(emptyTarget).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelled_clone_leaves_nothing_behind()
    {
        var source = _sandbox.CreateRepository();
        var target = Path.Combine(_sandbox.NewDirectory("clone"), "app");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Git.CloneAsync(source.Path, target, cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task Identity_comes_from_the_effective_configuration()
    {
        var repo = _sandbox.CreateRepository();

        (await Git.GetIdentityAsync(repo.Path, Ct)).Should().Be(new GitIdentity("Test User", "test@example.com"));
        (await Git.GetIdentityAsync(_sandbox.NewDirectory("elsewhere"), Ct)).Should().Be(new GitIdentity(GitSandbox.UserName, GitSandbox.UserEmail));
    }

    [Fact]
    public async Task Missing_identity_is_empty_and_can_be_set_globally()
    {
        using var sandbox = new GitSandbox(withIdentity: false);
        var folder = sandbox.NewDirectory("home");

        (await sandbox.Git.GetIdentityAsync(folder, Ct)).Should().Be(new GitIdentity(null, null));

        await sandbox.Git.SetGlobalIdentityAsync("Ada Lovelace", "ada@example.com", Ct);

        (await sandbox.Git.GetIdentityAsync(folder, Ct)).Should().Be(new GitIdentity("Ada Lovelace", "ada@example.com"));
        File.ReadAllText(sandbox.GlobalConfigPath).Should().Contain("Ada Lovelace");
    }

    [Theory]
    [InlineData("", "ada@example.com")]
    [InlineData("Ada", "")]
    [InlineData("Ada", "not-an-email")]
    public async Task Invalid_identity_is_rejected(string name, string email)
    {
        var act = () => Git.SetGlobalIdentityAsync(name, email, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Committing_without_identity_explains_how_to_fix_it()
    {
        using var sandbox = new GitSandbox(withIdentity: false);
        var folder = Path.Combine(sandbox.NewDirectory("repo"), "r");
        await sandbox.Git.InitAsync(folder, Ct);
        GitSandbox.WriteFile(folder, "a.txt", "a\n");

        var act = () => sandbox.Git.CommitAsync(folder, new GitCommitOptions("First", StageAll: true), Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Hint.Should().Contain("user.name");
    }

    [Fact]
    public async Task List_files_includes_tracked_and_untracked_but_not_ignored()
    {
        var repo = _sandbox.CreateRepository();
        repo.Commit("Files", (".gitignore", "bin/\n*.log\n"), ("src/app.cs", "x\n"));
        repo.WriteFile("notes.md", "n\n");
        repo.WriteFile("bin/app.dll", "binary");
        repo.WriteFile("debug.log", "log");
        repo.WriteFile("dossier/été.txt", "u\n");

        var files = await Git.ListFilesAsync(repo.Path, Ct);

        files.Should().BeEquivalentTo(".gitignore", "README.md", "src/app.cs", "notes.md", "dossier/été.txt");
    }

    public void Dispose() => _sandbox.Dispose();
}

/// <summary>Reports synchronously (Progress&lt;T&gt; would post to the thread pool and race the assertions).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Lock _gate = new();

    public void Report(T value)
    {
        lock (_gate)
        {
            report(value);
        }
    }
}
