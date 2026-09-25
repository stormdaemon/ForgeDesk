using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Analysis.Dependencies;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Analysis.Dependencies;

public sealed class DependencyScannerTests : IDisposable
{
    private readonly TempDirectory _dir = new("deps");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Parses_every_manifest_kind_root_first()
    {
        _dir.WriteFile("web/package.json", """{ "dependencies": { "vue": "^3.4.0" } }""");
        _dir.WriteFile("package.json", """{ "devDependencies": { "turbo": "2.0.0" } }""");
        _dir.WriteFile("Cargo.toml", "[dependencies]\nanyhow = \"1\"");
        _dir.WriteFile("go.mod", "module m\nrequire a.com/b v1.0.0");
        _dir.WriteFile("requirements-dev.txt", "pytest==8.2.0");
        _dir.WriteFile("pyproject.toml", "[project]\ndependencies = [\"httpx\"]");
        _dir.WriteFile("pom.xml", "<project><dependencies><dependency><groupId>g</groupId><artifactId>a</artifactId><version>1</version></dependency></dependencies></project>");
        _dir.WriteFile("build.gradle.kts", "dependencies {\n implementation(\"g:b:2\")\n}");
        _dir.WriteFile("composer.json", """{ "require": { "monolog/monolog": "^3" } }""");
        _dir.WriteFile("Gemfile", "gem \"rails\", \"~> 7.1\"");
        _dir.WriteFile("src/App/App.csproj", "<Project><ItemGroup><PackageReference Include=\"Dapper\" Version=\"2.1.0\" /></ItemGroup></Project>");

        var deps = await ScanAsync();

        deps.Select(d => (d.Ecosystem, d.Name)).Should().Equal(
            ("Gradle", "g:b"),
            ("Cargo", "anyhow"),
            ("Composer", "monolog/monolog"),
            ("RubyGems", "rails"),
            ("Go", "a.com/b"),
            ("npm", "turbo"),
            ("Maven", "g:a"),
            ("PyPI", "httpx"),
            ("PyPI", "pytest"),
            ("npm", "vue"),
            ("NuGet", "Dapper"));
        deps.Single(d => d.Name == "vue").Manifest.Should().Be("web/package.json");
    }

    [Fact]
    public async Task Uses_the_closest_central_package_versions()
    {
        _dir.WriteFile("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Dapper\" Version=\"2.1.89\" /></ItemGroup></Project>");
        _dir.WriteFile("legacy/Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Dapper\" Version=\"1.0.0\" /></ItemGroup></Project>");
        _dir.WriteFile("src/App/App.csproj", "<Project><ItemGroup><PackageReference Include=\"Dapper\" /></ItemGroup></Project>");
        _dir.WriteFile("legacy/Old/Old.csproj", "<Project><ItemGroup><PackageReference Include=\"Dapper\" /></ItemGroup></Project>");

        var deps = await ScanAsync();

        deps.Should().HaveCount(2);
        deps.Single(d => d.Manifest == "src/App/App.csproj").Version.Should().Be("2.1.89");
        deps.Single(d => d.Manifest == "legacy/Old/Old.csproj").Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task Resolves_cargo_workspace_and_gradle_catalog_versions()
    {
        _dir.WriteFile("Cargo.toml", "[workspace]\nmembers = [\"crates/*\"]\n[workspace.dependencies]\nserde = \"1.0.203\"");
        _dir.WriteFile("crates/core/Cargo.toml", "[dependencies]\nserde = { workspace = true }");
        _dir.WriteFile("gradle/libs.versions.toml", "[libraries]\nokhttp = \"com.squareup.okhttp3:okhttp:4.12.0\"");
        _dir.WriteFile("app/build.gradle.kts", "dependencies {\n implementation(libs.okhttp)\n}");

        var deps = await ScanAsync();

        deps.Select(d => (d.Name, d.Version)).Should().BeEquivalentTo([("serde", "1.0.203"), ("com.squareup.okhttp3:okhttp", "4.12.0")]);
    }

    [Fact]
    public async Task Caps_the_number_of_dependencies()
    {
        var many = string.Join(",", Enumerable.Range(0, 400).Select(i => $"\"pkg{i}\": \"1.0.{i}\""));
        _dir.WriteFile("a/package.json", $$"""{ "dependencies": { {{many}} } }""");
        _dir.WriteFile("b/package.json", $$"""{ "dependencies": { {{many}} } }""");

        var deps = await ScanAsync();

        deps.Should().HaveCount(AnalysisLimits.MaxDependencies);
    }

    [Fact]
    public async Task Skips_oversized_and_unreadable_manifests()
    {
        _dir.WriteFile("package.json", "{ \"dependencies\": { \"a\": \"1\" }, \"pad\": \"" + new string('x', AnalysisLimits.MaxManifestBytes) + "\" }");

        (await ScanAsync(["package.json", "missing/package.json"])).Should().BeEmpty();
    }

    private Task<IReadOnlyList<ForgeDesk.Core.Analysis.DependencyInfo>> ScanAsync(IEnumerable<string>? files = null) =>
        DependencyScanner.ScanAsync(
            _dir.Path,
            files ?? Directory.EnumerateFiles(_dir.Path, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_dir.Path, f).Replace('\\', '/')),
            TestContext.Current.CancellationToken);
}
