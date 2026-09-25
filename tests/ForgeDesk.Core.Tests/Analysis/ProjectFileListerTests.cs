using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Analysis;

public sealed class ProjectFileListerTests : IDisposable
{
    private readonly TempDirectory _dir = new("lister");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Walk_lists_files_breadth_first_and_skips_heavy_folders()
    {
        _dir.WriteFile("README.md", "readme");
        _dir.WriteFile("src/app/main.ts", "x");
        _dir.WriteFile(".github/workflows/ci.yml", "on: push");
        _dir.WriteFile(".gitignore", "dist/");
        foreach (var heavy in (string[])["node_modules/react/index.js", "bin/Debug/app.dll", "obj/x.json", "target/debug/app", "dist/app.js", "build/out.o",
                     ".venv/lib/site.py", "__pycache__/a.pyc", ".next/cache", ".nuxt/x", "packages/Foo/foo.nupkg", ".gradle/x", ".idea/x.xml", ".vs/x", ".git/HEAD", "venv/x"])
        {
            _dir.WriteFile(heavy, "heavy");
        }

        var listing = ProjectFileLister.Walk(_dir.Path, Ct);

        listing.FromGit.Should().BeFalse();
        listing.Truncated.Should().BeFalse();
        listing.Files.Select(f => f.RelativePath).Should().Equal(".gitignore", "README.md", ".github/workflows/ci.yml", "src/app/main.ts");
        listing.Files[1].Bytes.Should().Be(6);
    }

    [Fact]
    public void Walk_stops_at_the_file_cap()
    {
        for (var i = 0; i < 12; i++)
        {
            _dir.WriteFile($"f{i:00}.txt", "x");
        }

        var listing = ProjectFileLister.Walk(_dir.Path, Ct, maxFiles: 10);

        listing.Truncated.Should().BeTrue();
        listing.Files.Should().HaveCount(10);
    }

    [Fact]
    public void Walk_does_not_follow_directory_links()
    {
        _dir.WriteFile("real/file.txt", "x");
        try
        {
            Directory.CreateSymbolicLink(_dir.Combine("loop"), _dir.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links requires Developer Mode on Windows.");
        }

        var listing = ProjectFileLister.Walk(_dir.Path, Ct);

        listing.Files.Select(f => f.RelativePath).Should().Equal("real/file.txt");
    }

    [Fact]
    public void Git_listings_drop_dependency_folders_but_keep_source_like_ones()
    {
        foreach (var file in (string[])["src/a.cs", "bin/cli.js", "build/make.ps1", "packages/ui/index.ts", "node_modules/x/i.js", "dist/bundle.js", "obj/y.cs", "vendor/z.go"])
        {
            _dir.WriteFile(file, "content");
        }

        var listing = ProjectFileLister.FromGit(_dir.Path,
            ["src/a.cs", "bin/cli.js", "build/make.ps1", "packages/ui/index.ts", "node_modules/x/i.js", "dist/bundle.js", "obj/y.cs", "vendor/z.go", @"src\a.cs", "deleted.txt", ""], Ct);

        listing.FromGit.Should().BeTrue();
        listing.Files.Select(f => f.RelativePath).Should().Equal("src/a.cs", "bin/cli.js", "build/make.ps1", "packages/ui/index.ts", "vendor/z.go");
        listing.Files.Should().OnlyContain(f => f.Bytes == 7);
    }

    [Fact]
    public void Git_listings_stop_at_the_file_cap_keeping_root_files()
    {
        foreach (var file in (string[])["Assets/a.png", "Docs/b.md", "README.md", "src/c.cs", "zz.txt"])
        {
            _dir.WriteFile(file, "x");
        }

        var listing = ProjectFileLister.FromGit(_dir.Path, ["Assets/a.png", "Docs/b.md", "README.md", "src/c.cs", "zz.txt"], Ct, maxFiles: 3);

        listing.Truncated.Should().BeTrue();
        listing.Files.Select(f => f.RelativePath).Should().Equal("README.md", "zz.txt", "Assets/a.png");
    }

    [Fact]
    public async Task Falls_back_to_walking_when_git_cannot_list_files()
    {
        _dir.WriteFile("a.txt", "x");
        var git = Substitute.For<IGitService>();
        git.ListFilesAsync(_dir.Path, Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.GitCommandFailed, "ls-files failed"));
        var lister = new ProjectFileLister(git, NullLogger.Instance);

        var listing = await lister.ListAsync(_dir.Path, isRepository: true, Ct);

        listing.FromGit.Should().BeFalse();
        listing.Files.Should().ContainSingle().Which.RelativePath.Should().Be("a.txt");
    }
}
