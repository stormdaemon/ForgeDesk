using ForgeDesk.Core.Detection;

namespace ForgeDesk.Core.Tests.Detection;

public class LanguageStatisticsTests
{
    [Fact]
    public void Shares_are_computed_by_bytes_with_linguist_colors()
    {
        ScannedFile[] files =
        [
            new("src/App.cs", 6000),
            new("src/Model.cs", 2000),
            new("web/app.ts", 1500),
            new("web/view.tsx", 500),
            new("README.md", 9000),
            new("package.json", 3000),
        ];

        var shares = LanguageStatistics.Compute(files);

        shares.Select(s => s.Language).Should().Equal("C#", "TypeScript");
        shares[0].Should().Be(new LanguageShare("C#", 2, 8000, 80.0, "#178600"));
        shares[1].Should().Be(new LanguageShare("TypeScript", 2, 2000, 20.0, "#3178C6"));
    }

    [Fact]
    public void Vendored_generated_minified_documentation_and_lock_files_are_ignored()
    {
        ScannedFile[] files =
        [
            new("src/main.py", 100),
            new("vendor/lib.py", 10_000),
            new("third_party/x.c", 10_000),
            new("static/jquery.min.js", 10_000),
            new("app.bundle.js", 10_000),
            new("Forms/Main.Designer.cs", 10_000),
            new("obj/Debug/App.g.cs", 10_000),
            new("api/service.pb.go", 10_000),
            new("docs/conf.py", 10_000),
            new("wwwroot/lib/bootstrap/bootstrap.js", 10_000),
            new("dist/index.js", 10_000),
            new("package-lock.json", 10_000),
        ];

        var shares = LanguageStatistics.Compute(files);

        shares.Should().ContainSingle().Which.Language.Should().Be("Python");
    }

    [Fact]
    public void Languages_beyond_the_limit_are_merged_into_other()
    {
        var names = new[] { "a.cs", "b.ts", "c.py", "d.go", "e.rs", "f.java", "g.rb", "h.php", "i.swift", "j.kt" };
        var files = names.Select((name, index) => new ScannedFile(name, 1000 - (index * 10))).ToList();

        var shares = LanguageStatistics.Compute(files);

        shares.Should().HaveCount(LanguageStatistics.MaxLanguages + 1);
        var other = shares[^1];
        other.Language.Should().Be(LanguageStatistics.OtherLanguage);
        other.Files.Should().Be(3);
        other.Color.Should().Be(LanguageCatalog.OtherColor);
        shares.Sum(s => s.Percentage).Should().BeApproximately(100, 0.5);
    }

    [Fact]
    public void Data_and_prose_count_only_when_there_is_no_code()
    {
        var shares = LanguageStatistics.Compute([new ScannedFile("README.md", 500), new ScannedFile("config.yml", 100)]);

        shares.Select(s => s.Language).Should().Equal("Markdown", "YAML");
    }

    [Fact]
    public void Empty_files_fall_back_to_file_counts()
    {
        var shares = LanguageStatistics.Compute([new ScannedFile("a.go", 0), new ScannedFile("b.go", 0), new ScannedFile("c.rs", 0)]);

        shares.Select(s => (s.Language, s.Percentage)).Should().Equal(("Go", 66.7), ("Rust", 33.3));
    }

    [Theory]
    [InlineData("Dockerfile", "Dockerfile")]
    [InlineData("Dockerfile.dev", "Dockerfile")]
    [InlineData("src/CMakeLists.txt", "CMake")]
    [InlineData("GNUmakefile", "Makefile")]
    [InlineData("views/home.blade.php", "Blade")]
    [InlineData("index.php", "PHP")]
    [InlineData("App.xaml", "XAML")]
    [InlineData("Page.razor", "Razor")]
    [InlineData("scripts/deploy.PS1", "PowerShell")]
    [InlineData("Gemfile", "Ruby")]
    [InlineData("jquery.min.js", "JavaScript")]
    public void Catalog_resolves_file_names_and_extensions(string path, string language) =>
        LanguageCatalog.Find(path)!.Name.Should().Be(language);

    [Fact]
    public void Catalog_covers_the_common_languages()
    {
        LanguageCatalog.Count.Should().BeGreaterThanOrEqualTo(60);
        LanguageCatalog.Find("notes.unknownext").Should().BeNull();
        LanguageCatalog.ColorOf("Rust").Should().Be("#DEA584");
    }
}
