using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Detection;

public class ProjectDetectorTests
{
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Profile_summarizes_files_languages_structure_and_important_files()
    {
        using var fixture = new DetectionFixture()
            .With("README.md", "# Demo")
            .With("LICENSE", "MIT")
            .With("CHANGELOG.md", "")
            .With(".github/CONTRIBUTING.md", "")
            .With("SECURITY.md", "")
            .With(".gitignore", "bin/")
            .With(".editorconfig", "root = true")
            .With("Dockerfile", "FROM scratch")
            .With("src/main.go", new string('x', 400))
            .With("tests/main_test.go", "package main")
            .With("docs/guide.md", "")
            .With("node_modules/left-pad/index.js", "module.exports = 1;")
            .With("bin/Debug/app.dll", "binary")
            .With("scripts/release.ps1", "");

        var profile = await fixture.DetectAsync();

        profile.DetectedAt.Should().Be(Clock.Now);
        profile.PrimaryLanguage.Should().Be("Go");
        profile.FileCount.Should().Be(12, "node_modules and bin are not scanned");
        profile.TotalBytes.Should().BeGreaterThan(400);
        profile.ScanTruncated.Should().BeFalse();
        profile.ImportantFiles.Should().Equal("README.md", "LICENSE", "CHANGELOG.md", ".github/CONTRIBUTING.md", "SECURITY.md", ".gitignore", ".editorconfig", "Dockerfile");

        profile.Structure.Should().ContainEquivalentOf(new StructureEntry("src", true, StructureKind.Source, "Source code"));
        profile.Structure.Should().ContainEquivalentOf(new StructureEntry("tests", true, StructureKind.Tests, "Tests"));
        profile.Structure.Should().ContainEquivalentOf(new StructureEntry("node_modules", true, StructureKind.Dependencies, "npm packages (not scanned)"));
        profile.Structure.Should().ContainEquivalentOf(new StructureEntry("bin", true, StructureKind.Build, "Build output (not scanned)"));
        profile.Structure.Should().ContainEquivalentOf(new StructureEntry(".github", true, StructureKind.Ci, "GitHub workflows and templates"));
        profile.Structure.Should().ContainEquivalentOf(new StructureEntry("README.md", false, StructureKind.Docs, "Project overview"));
        profile.Structure.Should().ContainEquivalentOf(new StructureEntry("Dockerfile", false, StructureKind.Build, "Container image"));
        profile.Structure.TakeWhile(e => e.IsDirectory).Should().HaveCount(profile.Structure.Count(e => e.IsDirectory), "folders are listed first");
        profile.Tests.HasTests.Should().BeTrue();
        profile.Tests.Locations.Should().Contain("tests");
    }

    [Fact]
    public async Task Scan_stops_at_the_file_cap_and_says_so()
    {
        using var dir = new TempDirectory("big");
        dir.WriteFile("package.json", """{ "scripts": { "build": "tsc" } }""");
        for (var i = 0; i < 40; i++)
        {
            dir.WriteFile($"src/deep/file{i:D2}.ts", "export {};");
        }

        var detector = CreateDetector(Substitute.For<IGitService>(), maxFiles: 10);

        var profile = await detector.DetectAsync(dir.Path, TestContext.Current.CancellationToken);

        profile.ScanTruncated.Should().BeTrue();
        profile.FileCount.Should().Be(10);
        profile.Notes[0].Should().Contain("only the first");
        profile.HasCommand("npm:build").Should().BeTrue("root files are scanned first");
    }

    [Fact]
    public async Task Git_listing_is_used_for_repositories()
    {
        using var repo = TestRepository.Create();
        repo.WriteFile("src/app.py", string.Concat(Enumerable.Repeat("print('hello world')\n", 10)));
        repo.WriteFile("build/package.ps1", "Write-Host pack\n");
        repo.WriteFile("ignored.log", "noise");
        repo.WriteFile("node_modules/pkg/index.js", "committed by mistake");
        var git = Substitute.For<IGitService>();
        git.ListFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["README.md", "src/app.py", "build/package.ps1", "node_modules/pkg/index.js", "deleted.txt"]);

        var profile = await CreateDetector(git).DetectAsync(repo.Path, TestContext.Current.CancellationToken);

        await git.Received(1).ListFilesAsync(Arg.Is<string>(p => PathUtil.AreSame(p, repo.Path)), Arg.Any<CancellationToken>());
        profile.FileCount.Should().Be(3, "dependency folders are dropped and missing files have no size");
        profile.PrimaryLanguage.Should().Be("Python");
        profile.Structure.Should().ContainEquivalentOf(new StructureEntry("build", true, StructureKind.Build, "Build and packaging scripts"));
    }

    [Fact]
    public async Task Walker_is_used_when_git_listing_fails()
    {
        using var repo = TestRepository.Create();
        repo.WriteFile("main.rs", "fn main() {}");
        var git = Substitute.For<IGitService>();
        git.ListFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(ErrorKind.GitNotFound, "Git is not installed."));

        var profile = await CreateDetector(git).DetectAsync(repo.Path, TestContext.Current.CancellationToken);

        profile.FileCount.Should().Be(2);
        profile.PrimaryLanguage.Should().Be("Rust");
    }

    [Fact]
    public async Task Failing_detector_adds_a_note_and_others_still_contribute()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("Makefile", "build:\n\techo\n");
        var failing = Substitute.For<IEcosystemDetector>();
        failing.Name.Returns("Broken");
        failing.ContributeAsync(Arg.Any<DetectionContext>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var detector = new ProjectDetector([failing, new ForgeDesk.Core.Detection.Ecosystems.MakeDetector()], Substitute.For<IGitService>(), Clock);

        var profile = await detector.DetectAsync(dir.Path, TestContext.Current.CancellationToken);

        profile.Notes.Should().ContainSingle(n => n.Contains("Broken", StringComparison.Ordinal) && n.Contains("boom", StringComparison.Ordinal));
        profile.HasCommand("make:build").Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var dir = new TempDirectory();
        using var cts = new CancellationTokenSource();
        var cancelling = Substitute.For<IEcosystemDetector>();
        cancelling.ContributeAsync(Arg.Any<DetectionContext>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var detector = new ProjectDetector([cancelling], Substitute.For<IGitService>(), Clock);

        var act = () => detector.DetectAsync(dir.Path, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Missing_folder_is_reported()
    {
        var detector = CreateDetector(Substitute.For<IGitService>());

        var act = () => detector.DetectAsync(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);
    }

    [Fact]
    public async Task Commands_are_deduplicated_and_sorted_by_category_then_name()
    {
        using var fixture = new DetectionFixture()
            .With("package.json", """{ "scripts": { "test": "vitest", "dev": "vite", "build": "vite build", "zeta": "echo", "alpha": "echo" } }""")
            .With("Makefile", "build:\n\tnpm run build\n");

        var profile = await fixture.DetectAsync();

        profile.Commands.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        profile.Commands.Select(c => c.Category).Should().StartWith([CommandCategory.Dev, CommandCategory.Run, CommandCategory.Run, CommandCategory.Build, CommandCategory.Build]);
        profile.Commands.Where(c => c.Category == CommandCategory.Run).Select(c => c.Name).Should().Equal("alpha", "zeta");
        profile.CommandsIn(CommandCategory.Test).Should().ContainSingle();
    }

    [Fact]
    public void SortCommands_keeps_the_first_command_for_a_duplicated_id()
    {
        var sorted = ProjectDetector.SortCommands(
        [
            new DetectedCommand { Id = "x:1", Name = "First", CommandLine = "a", Source = "s", Category = CommandCategory.Other },
            new DetectedCommand { Id = "x:1", Name = "Second", CommandLine = "b", Source = "s", Category = CommandCategory.Dev },
            new DetectedCommand { Id = "x:2", Name = "Deploy", CommandLine = "c", Source = "s", Category = CommandCategory.Deploy },
        ]);

        sorted.Select(c => c.Name).Should().Equal("Deploy", "First");
    }

    [Fact]
    public async Task Symbolic_links_are_not_followed()
    {
        using var outside = new TempDirectory("outside");
        outside.WriteFile("secret.py", new string('x', 1000));
        using var dir = new TempDirectory();
        dir.WriteFile("main.go", "package main");
        try
        {
            Directory.CreateSymbolicLink(dir.Combine("linked"), outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links requires Developer Mode or elevation on this machine.");
        }

        var profile = await CreateDetector(Substitute.For<IGitService>()).DetectAsync(dir.Path, TestContext.Current.CancellationToken);

        profile.FileCount.Should().Be(1);
        profile.PrimaryLanguage.Should().Be("Go");
    }

    private static ProjectDetector CreateDetector(IGitService git, int maxFiles = ProjectDetector.DefaultMaxFiles) =>
        new(
        [
            new ForgeDesk.Core.Detection.Ecosystems.NodeDetector(),
            new ForgeDesk.Core.Detection.Ecosystems.PythonDetector(),
            new ForgeDesk.Core.Detection.Ecosystems.RustDetector(),
        ], git, Clock)
        { MaxFiles = maxFiles };
}
