using System.Collections.Concurrent;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Core.Processes;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Files;

public class ContentSearchServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ContentSearchService CreateService(bool useGit) => new(new GitCli(ProcessRunner.Instance)) { UseGit = useGit };

    private static async Task<(List<ContentMatch> Matches, ContentSearchSummary Summary)> SearchAsync(
        string root, ContentSearchQuery query, bool useGit = true)
    {
        var matches = new ConcurrentQueue<ContentMatch>();
        var summary = await CreateService(useGit).SearchAsync(root, query, matches.Enqueue, Ct);
        return ([.. matches.OrderBy(m => m.RelativePath, StringComparer.Ordinal).ThenBy(m => m.LineNumber)], summary);
    }

    private static TestRepository CreateSampleRepository()
    {
        var repo = TestRepository.Create();
        repo.WriteFile(".gitignore", "ignored.txt\n");
        repo.Commit("sources",
            ("src/app.cs", "class App\n{\n    // TODO: wire the Hello service\n    void hello() { }\n}\n"),
            ("src/util.ts", "export const helloWorld = 1;\nconst x = 'HELLO';\n"),
            ("docs/guide.md", "Say hello to the team.\n"));
        repo.WriteFile("notes.txt", "untracked hello note\n");
        repo.WriteFile("ignored.txt", "hello from an ignored file\n");
        return repo;
    }

    [Fact]
    public async Task Git_grep_finds_tracked_and_untracked_files_but_not_ignored_ones()
    {
        using var repo = CreateSampleRepository();

        var (matches, summary) = await SearchAsync(repo.Path, new ContentSearchQuery { Pattern = "hello" });

        summary.Engine.Should().Be(ContentSearchService.GitEngine);
        matches.Select(m => m.RelativePath).Distinct().Should().BeEquivalentTo(["docs/guide.md", "notes.txt", "src/app.cs", "src/util.ts"]);
        summary.Matches.Should().Be(matches.Count);
        summary.FilesMatched.Should().Be(4);
        summary.IsTruncated.Should().BeFalse();

        var appLines = matches.Where(m => m.RelativePath == "src/app.cs").ToList();
        appLines.Select(m => m.LineNumber).Should().Equal(3, 4);
        appLines[0].Should().Be(new ContentMatch("src/app.cs", 3, 23, 5, "    // TODO: wire the Hello service"));
    }

    [Fact]
    public async Task Git_null_output_format_is_path_line_column_text()
    {
        // Pins the exact format the parser relies on: "path\0line\0column\0text".
        using var repo = TestRepository.Create();
        repo.Commit("add",
            ("dir/with space/plain.txt", "first\nsecond: needle here\n"),
            ("dir/é/ünïcode.txt", "✓✓ needle\n"));

        // Raw output of an ASCII path only: the test helper decodes git's output with the console code page.
        var output = repo.Git("grep", "-n", "--column", "-I", "--no-color", "--null", "-F", "-e", "needle", "--", "dir/with space");
        output.Should().Be("dir/with space/plain.txt\u00002\u00009\u0000second: needle here");

        // Git's column counts bytes ("✓" is 3 bytes); results count characters.
        var (matches, _) = await SearchAsync(repo.Path, new ContentSearchQuery { Pattern = "needle" });
        matches.Should().Equal(
            new ContentMatch("dir/with space/plain.txt", 2, 9, 6, "second: needle here"),
            new ContentMatch("dir/é/ünïcode.txt", 1, 4, 6, "✓✓ needle"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Case_sensitivity_whole_word_and_regex_options_apply(bool useGit)
    {
        using var repo = CreateSampleRepository();

        var caseSensitive = await SearchAsync(repo.Path, new ContentSearchQuery { Pattern = "HELLO", MatchCase = true }, useGit);
        caseSensitive.Matches.Should().ContainSingle().Which.RelativePath.Should().Be("src/util.ts");

        var wholeWord = await SearchAsync(repo.Path, new ContentSearchQuery { Pattern = "hello", WholeWord = true, PathFilter = "src/**" }, useGit);
        wholeWord.Matches.Select(m => (m.RelativePath, m.LineNumber)).Should().Equal(("src/app.cs", 3), ("src/app.cs", 4), ("src/util.ts", 2));

        var regex = await SearchAsync(repo.Path, new ContentSearchQuery { Pattern = @"hello\w+", IsRegex = true }, useGit);
        regex.Matches.Should().ContainSingle().Which.Should().Match<ContentMatch>(m => m.RelativePath == "src/util.ts" && m.Column == 14 && m.Length == 10);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Path_filter_limits_the_searched_files(bool useGit)
    {
        using var repo = CreateSampleRepository();

        var (matches, _) = await SearchAsync(repo.Path, new ContentSearchQuery { Pattern = "hello", PathFilter = "*.cs, *.md" }, useGit);
        matches.Select(m => m.RelativePath).Distinct().Should().BeEquivalentTo(["src/app.cs", "docs/guide.md"]);

        var (excluded, _) = await SearchAsync(repo.Path, new ContentSearchQuery { Pattern = "hello", PathFilter = "!src" }, useGit);
        excluded.Should().NotContain(m => m.RelativePath.StartsWith("src/", StringComparison.Ordinal));
        excluded.Should().Contain(m => m.RelativePath == "docs/guide.md");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stops_at_max_results(bool useGit)
    {
        using var dir = new TempDirectory();
        for (var i = 0; i < 20; i++)
        {
            dir.WriteFile($"f{i:00}.txt", string.Concat(Enumerable.Repeat("match me\n", 50)));
        }

        var (matches, summary) = await SearchAsync(dir.Path, new ContentSearchQuery { Pattern = "match", MaxResults = 30 }, useGit);

        matches.Should().HaveCount(30);
        summary.Matches.Should().Be(30);
        summary.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Non_repository_folders_use_git_without_index_and_skip_heavy_folders()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/a.js", "needle\n");
        dir.WriteFile("node_modules/lib/index.js", "needle\n");
        dir.WriteFile("bin/Debug/out.txt", "needle\n");

        var (matches, summary) = await SearchAsync(dir.Path, new ContentSearchQuery { Pattern = "needle" });

        summary.Engine.Should().Be(ContentSearchService.GitNoIndexEngine);
        matches.Should().ContainSingle().Which.RelativePath.Should().Be("src/a.js");
    }

    [Fact]
    public async Task Managed_engine_skips_heavy_folders_binaries_and_large_files()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("src/a.js", "needle\n");
        dir.WriteFile("node_modules/lib/index.js", "needle\n");
        await File.WriteAllBytesAsync(dir.Combine("blob.bin"), [.. "needle"u8, 0, 1, 2], Ct);
        dir.WriteFile("huge.txt", "needle\n" + new string('x', (int)ManagedContentSearcher.MaxFileSize));

        var (matches, summary) = await SearchAsync(dir.Path, new ContentSearchQuery { Pattern = "needle" }, useGit: false);

        summary.Engine.Should().Be(ContentSearchService.ManagedEngine);
        matches.Should().ContainSingle().Which.RelativePath.Should().Be("src/a.js");
    }

    [Fact]
    public async Task Managed_engine_reads_legacy_and_bom_encodings()
    {
        using var dir = new TempDirectory();
        await File.WriteAllBytesAsync(dir.Combine("latin.txt"), [0x63, 0x61, 0x66, 0xE9, 0x0A], Ct);
        await File.WriteAllBytesAsync(dir.Combine("utf16.txt"), [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes("line\r\nsay café\r\n")], Ct);

        var (matches, _) = await SearchAsync(dir.Path, new ContentSearchQuery { Pattern = "café" }, useGit: false);

        matches.Select(m => (m.RelativePath, m.LineNumber, m.LineText)).Should().Equal(("latin.txt", 1, "café"), ("utf16.txt", 2, "say café"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_regex_is_invalid_input(bool useGit)
    {
        using var dir = new TempDirectory();
        var act = () => CreateService(useGit).SearchAsync(dir.Path, new ContentSearchQuery { Pattern = "(unclosed", IsRegex = true }, _ => { }, Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_is_honored(bool useGit)
    {
        using var dir = new TempDirectory();
        for (var i = 0; i < 200; i++)
        {
            dir.WriteFile($"d{i % 10}/f{i}.txt", string.Concat(Enumerable.Repeat("some text to scan\n", 200)));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();

        var act = () => CreateService(useGit).SearchAsync(dir.Path, new ContentSearchQuery { Pattern = "text" }, _ => { }, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cancelling_mid_search_stops_promptly()
    {
        using var dir = new TempDirectory();
        for (var i = 0; i < 300; i++)
        {
            dir.WriteFile($"d{i % 10}/f{i}.txt", string.Concat(Enumerable.Repeat("some text to scan\n", 300)));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var received = 0;
        var act = () => CreateService(useGit: false).SearchAsync(dir.Path, new ContentSearchQuery { Pattern = "text", MaxResults = 1_000_000 }, _ =>
        {
            if (Interlocked.Increment(ref received) == 10)
            {
                cts.Cancel();
            }
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        received.Should().BeLessThan(300 * 300);
    }

    [Fact]
    public async Task Missing_folder_is_path_not_found()
    {
        using var dir = new TempDirectory();
        var act = () => CreateService(useGit: true).SearchAsync(dir.Combine("missing"), new ContentSearchQuery { Pattern = "x" }, _ => { }, Ct);
        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);
    }
}
