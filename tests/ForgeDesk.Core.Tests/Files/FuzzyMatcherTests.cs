using System.Diagnostics;
using ForgeDesk.Core.Files;

namespace ForgeDesk.Core.Tests.Files;

public class FuzzyMatcherTests
{
    private static readonly string[] Paths =
    [
        "README.md",
        "src/ForgeDesk.Core/Files/FileService.cs",
        "src/ForgeDesk.Core/Files/FileIndex.cs",
        "src/ForgeDesk.Core/Files/IFileService.cs",
        "src/ForgeDesk.Core/Runs/RunService.cs",
        "src/ForgeDesk.App/MainWindow.xaml",
        "src/ForgeDesk.App/MainWindow.xaml.cs",
        "tests/ForgeDesk.Core.Tests/Files/FileServiceTests.cs",
        "docs/ARCHITECTURE.md",
        "docs/file_service_notes.md",
        "package.json",
        "web/src/components/user-profile/UserProfile.tsx",
        "web/src/utils/format.ts",
        "scripts/build.ps1",
    ];

    private static IReadOnlyList<FileMatch> Search(string query, int max = 10) => FuzzyMatcher.Create(Paths).Search(query, max);

    [Fact]
    public void Exact_file_name_ranks_first()
    {
        Search("FileService.cs")[0].RelativePath.Should().Be("src/ForgeDesk.Core/Files/FileService.cs");
        Search("package.json")[0].RelativePath.Should().Be("package.json");
        Search("readme")[0].RelativePath.Should().Be("README.md");
    }

    [Fact]
    public void File_name_matches_beat_directory_matches()
    {
        var results = Search("format");
        results[0].RelativePath.Should().Be("web/src/utils/format.ts");
    }

    [Fact]
    public void Word_starts_and_camel_humps_are_preferred()
    {
        // "File|Service" (camel hump) and "file_|service" (separator) are equally good; mid-word hits rank below.
        var fs = Search("fs").Select(m => m.RelativePath).ToList();
        fs.Take(2).Should().BeEquivalentTo(["docs/file_service_notes.md", "src/ForgeDesk.Core/Files/FileService.cs"]);
        fs.IndexOf("src/ForgeDesk.Core/Files/FileService.cs").Should().BeLessThan(fs.IndexOf("src/ForgeDesk.Core/Files/IFileService.cs"));

        Search("mwx")[0].RelativePath.Should().Be("src/ForgeDesk.App/MainWindow.xaml");
        Search("upt")[0].RelativePath.Should().Be("web/src/components/user-profile/UserProfile.tsx");
    }

    [Fact]
    public void Shorter_paths_win_ties()
    {
        var results = Search("mainwindow");
        results[0].RelativePath.Should().Be("src/ForgeDesk.App/MainWindow.xaml");
        results[1].RelativePath.Should().Be("src/ForgeDesk.App/MainWindow.xaml.cs");
    }

    [Fact]
    public void Directory_and_file_parts_can_be_combined()
    {
        var results = Search("testsfileservice");
        results.Should().ContainSingle().Which.RelativePath.Should().Be("tests/ForgeDesk.Core.Tests/Files/FileServiceTests.cs");

        Search("runs/runservice")[0].RelativePath.Should().Be("src/ForgeDesk.Core/Runs/RunService.cs");
        Search(@"runs\runservice")[0].RelativePath.Should().Be("src/ForgeDesk.Core/Runs/RunService.cs");
    }

    [Fact]
    public void Non_matching_paths_are_excluded()
    {
        Search("zzz").Should().BeEmpty();
        Search("xaml").Select(r => r.RelativePath).Should().BeEquivalentTo(["src/ForgeDesk.App/MainWindow.xaml", "src/ForgeDesk.App/MainWindow.xaml.cs"]);
    }

    [Fact]
    public void Matched_indices_point_at_the_highlighted_characters()
    {
        foreach (var query in new[] { "fs", "fileservice", "mwx", "runs/rs", "USER" })
        {
            foreach (var match in Search(query))
            {
                var normalized = query.Replace('\\', '/').ToLowerInvariant();
                match.MatchedIndices.Should().HaveCount(normalized.Length);
                match.MatchedIndices.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
                new string(match.MatchedIndices.Select(i => char.ToLowerInvariant(match.RelativePath[i])).ToArray()).Should().Be(normalized);
            }
        }
    }

    [Fact]
    public void Highlights_prefer_segment_starts()
    {
        var match = Search("fs").First(m => m.RelativePath == "docs/file_service_notes.md");
        match.MatchedIndices.Should().Equal(5, 10);
    }

    [Fact]
    public void Query_is_case_insensitive_and_ignores_spaces()
    {
        Search("FILE SERVICE")[0].RelativePath.Should().Be("src/ForgeDesk.Core/Files/FileService.cs");
    }

    [Fact]
    public void Empty_query_returns_shallow_short_paths_first()
    {
        var results = Search("", max: 3);
        results.Select(r => r.RelativePath).Should().Equal("README.md", "package.json", "scripts/build.ps1");
        results.Should().OnlyContain(r => r.MatchedIndices.Count == 0);
    }

    [Fact]
    public void Respects_max_and_handles_empty_lists()
    {
        Search("s", max: 2).Should().HaveCount(2);
        Search("s", max: 0).Should().BeEmpty();
        FuzzyMatcher.Create([]).Search("abc", 10).Should().BeEmpty();
    }

    [Fact]
    public void Very_long_paths_and_queries_are_handled()
    {
        var longPath = string.Join('/', Enumerable.Range(0, 200).Select(i => $"segment{i}")) + "/target.txt";
        var matcher = FuzzyMatcher.Create([longPath, "target.txt"]);

        matcher.Search("target", 10).Select(m => m.RelativePath).Should().Equal("target.txt", longPath);
        matcher.Search(new string('x', 1000), 10).Should().BeEmpty();
    }

    [Fact]
    public void Searches_300k_paths_quickly()
    {
        var random = new Random(42);
        string[] words = ["src", "lib", "core", "app", "service", "controller", "model", "view", "util", "test", "api", "data", "config", "user", "order", "payment"];
        var paths = new string[300_000];
        for (var i = 0; i < paths.Length; i++)
        {
            var depth = random.Next(2, 7);
            var segments = new string[depth];
            for (var d = 0; d < depth - 1; d++)
            {
                segments[d] = words[random.Next(words.Length)] + (random.Next(3) == 0 ? random.Next(100).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            }

            segments[^1] = $"{words[random.Next(words.Length)]}{words[random.Next(words.Length)]}{i}.cs";
            paths[i] = string.Join('/', segments);
        }

        paths[123_456] = "src/payments/checkout/PaymentGatewayController.cs";
        var matcher = FuzzyMatcher.Create(paths);
        matcher.Search("warmup", 50);

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<FileMatch> results = [];
        foreach (var query in new[] { "pgc", "paymentgateway", "srvctrl", "u", "coreapitest" })
        {
            results = matcher.Search(query, 100);
            if (query == "paymentgateway")
            {
                results[0].RelativePath.Should().Be("src/payments/checkout/PaymentGatewayController.cs");
            }
        }

        stopwatch.Stop();

        // Five queries; the product target is < 150 ms per query in Release, the bound here is generous for Debug builds and slow CI machines.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        results.Should().NotBeEmpty();
    }
}
