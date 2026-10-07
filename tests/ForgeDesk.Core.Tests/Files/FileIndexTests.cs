using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Files;

public class FileIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FileIndex CreateIndex(bool useGit = true) => new(new GitCli(ProcessRunner.Instance)) { UseGit = useGit };

    [Fact]
    public async Task Repository_index_lists_tracked_and_untracked_files_without_ignored_or_deleted_ones()
    {
        using var repo = TestRepository.Create();
        repo.WriteFile(".gitignore", "*.log\n");
        repo.Commit("files",
            ("src/app.cs", "x"),
            ("build/package.ps1", "x"),
            ("old.txt", "x"),
            ("dir with space/é.txt", "x"));
        File.Delete(repo.Combine("old.txt"));
        repo.WriteFile("new.txt", "x");
        repo.WriteFile("debug.log", "x");
        repo.WriteFile("node_modules/lib/index.js", "x");

        var snapshot = await CreateIndex().GetAsync(repo.Path, cancellationToken: Ct);

        snapshot.Files.Should().BeEquivalentTo([".gitignore", "README.md", "build/package.ps1", "dir with space/é.txt", "new.txt", "src/app.cs"]);
        snapshot.Files.Should().BeInAscendingOrder(StringComparer.Ordinal);
        snapshot.IsTruncated.Should().BeFalse();
        snapshot.Root.Should().Be(PathUtil.Normalize(repo.Path));
    }

    [Fact]
    public async Task Submodules_are_indexed_by_their_files_not_as_a_file()
    {
        using var sub = TestRepository.Create();
        sub.Commit("sub", ("s.txt", "x"), ("lib/deep.cs", "x"));
        using var repo = TestRepository.Create();
        repo.Git("-c", "protocol.file.allow=always", "submodule", "add", sub.Path, "libs/sub");
        repo.Git("commit", "-m", "add submodule");

        var snapshot = await CreateIndex().GetAsync(repo.Path, cancellationToken: Ct);

        snapshot.Files.Should().Contain(["libs/sub/s.txt", "libs/sub/lib/deep.cs", "libs/sub/README.md", ".gitmodules"]);
        snapshot.Files.Should().NotContain("libs/sub");
    }

    [Fact]
    public async Task Untracked_nested_repositories_are_indexed_by_their_files()
    {
        using var repo = TestRepository.Create();
        repo.Git("init", "inner");
        repo.WriteFile("inner/x.txt", "x");

        var snapshot = await CreateIndex().GetAsync(repo.Path, cancellationToken: Ct);

        snapshot.Files.Should().Contain("inner/x.txt");
        snapshot.Files.Should().NotContain(f => f.EndsWith('/') || f == "inner");
    }

    [Fact]
    public async Task Files_outside_the_sparse_checkout_are_not_indexed()
    {
        using var repo = TestRepository.Create();
        repo.Commit("files", ("a/x.txt", "x"), ("b/y.txt", "x"));
        repo.Git("sparse-checkout", "set", "a");
        File.Exists(repo.Combine("b", "y.txt")).Should().BeFalse();

        var snapshot = await CreateIndex().GetAsync(repo.Path, cancellationToken: Ct);

        snapshot.Files.Should().BeEquivalentTo(["README.md", "a/x.txt"]);
    }

    [Fact]
    public async Task Skip_worktree_files_present_on_disk_stay_indexed()
    {
        using var repo = TestRepository.Create();
        repo.Commit("files", ("config.json", "{}"));
        repo.Git("update-index", "--skip-worktree", "config.json");

        var snapshot = await CreateIndex().GetAsync(repo.Path, cancellationToken: Ct);

        snapshot.Files.Should().BeEquivalentTo(["README.md", "config.json"]);
    }

    [Fact]
    public async Task Repository_subfolder_lists_paths_relative_to_the_project()
    {
        using var repo = TestRepository.Create();
        repo.Commit("files", ("web/src/index.ts", "x"), ("api/main.go", "x"));

        var snapshot = await CreateIndex().GetAsync(repo.Combine("web"), cancellationToken: Ct);

        snapshot.Files.Should().Equal("src/index.ts");
    }

    [Fact]
    public async Task Plain_folders_are_walked_without_heavy_folders()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/main.py", "x");
        dir.WriteFile(".env", "x");
        dir.WriteFile("node_modules/a/index.js", "x");
        dir.WriteFile("src/__pycache__/main.pyc", "x");
        dir.WriteFile(".venv/lib/site.py", "x");
        dir.WriteFile("deep/er/still/file.txt", "x");

        var snapshot = await CreateIndex().GetAsync(dir.Path, cancellationToken: Ct);

        snapshot.Files.Should().Equal(".env", "deep/er/still/file.txt", "src/main.py");
    }

    [Fact]
    public async Task Snapshots_are_cached_until_invalidated_or_refreshed()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", "x");
        var index = CreateIndex();

        var first = await index.GetAsync(dir.Path, cancellationToken: Ct);
        dir.WriteFile("b.txt", "x");
        var cached = await index.GetAsync(dir.Path + Path.DirectorySeparatorChar, cancellationToken: Ct);
        cached.Should().BeSameAs(first);

        index.Invalidate(dir.Path);
        var rebuilt = await index.GetAsync(dir.Path, cancellationToken: Ct);
        rebuilt.Should().NotBeSameAs(first);
        rebuilt.Files.Should().Equal("a.txt", "b.txt");

        dir.WriteFile("c.txt", "x");
        var refreshed = await index.GetAsync(dir.Path, forceRefresh: true, Ct);
        refreshed.Files.Should().HaveCount(3);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_build()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("a.txt", "x");
        var index = CreateIndex();

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => index.GetAsync(dir.Path, cancellationToken: Ct)));

        snapshots.Should().AllSatisfy(s => s.Should().BeSameAs(snapshots[0]));
    }

    [Fact]
    public async Task Missing_folder_is_path_not_found_and_not_cached()
    {
        using var dir = new TempDirectory();
        var missing = dir.Combine("later");
        var index = CreateIndex();

        var act = () => index.GetAsync(missing, cancellationToken: Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);

        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "now.txt"), "x");
        (await index.GetAsync(missing, cancellationToken: Ct)).Files.Should().Equal("now.txt");
    }

    [Fact]
    public async Task Search_uses_the_snapshot()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/Services/UserService.cs", "x");
        dir.WriteFile("src/Views/UserView.xaml", "x");
        var index = CreateIndex();
        var snapshot = await index.GetAsync(dir.Path, cancellationToken: Ct);

        var results = index.Search(snapshot, "usrsvc");

        results.Should().ContainSingle().Which.RelativePath.Should().Be("src/Services/UserService.cs");
    }
}
