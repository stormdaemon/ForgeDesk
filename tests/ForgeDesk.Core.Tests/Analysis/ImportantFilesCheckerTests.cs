using ForgeDesk.Core.Analysis;

namespace ForgeDesk.Core.Tests.Analysis;

public class ImportantFilesCheckerTests
{
    private static readonly HashSet<string> NoEcosystems = [];

    [Fact]
    public void Reports_every_check_with_path_and_reason()
    {
        var findings = ImportantFilesChecker.Check(
            ["readme.md", "LICENSE-MIT", ".gitignore", ".github/workflows/ci.yml", ".github/CONTRIBUTING.md", "CHANGELOG.md", "docs/SECURITY.md", ".editorconfig", "src/a.cs"],
            hasTests: true, "tests/", NoEcosystems);

        findings.Select(f => (f.Kind, f.Check.Present, f.Check.Path)).Should().Equal(
            (ImportantFileKind.Readme, true, "readme.md"),
            (ImportantFileKind.License, true, "LICENSE-MIT"),
            (ImportantFileKind.GitIgnore, true, ".gitignore"),
            (ImportantFileKind.CiWorkflow, true, ".github/workflows/ci.yml"),
            (ImportantFileKind.Tests, true, "tests/"),
            (ImportantFileKind.Contributing, true, ".github/CONTRIBUTING.md"),
            (ImportantFileKind.Changelog, true, "CHANGELOG.md"),
            (ImportantFileKind.Security, true, "docs/SECURITY.md"),
            (ImportantFileKind.EditorConfig, true, ".editorconfig"));
        findings.Should().OnlyContain(f => f.Check.Why.Length > 10 && f.Check.Label.Length > 0);
    }

    [Fact]
    public void Missing_files_are_reported_absent()
    {
        var findings = ImportantFilesChecker.Check(["src/main.go", "docs/guide/README.md", "src/LICENSE"], hasTests: false, null, NoEcosystems);

        findings.Should().OnlyContain(f => !f.Check.Present && f.Check.Path == null);
    }

    [Theory]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("azure-pipelines.yml")]
    [InlineData(".circleci/config.yml")]
    [InlineData("Jenkinsfile")]
    [InlineData(".github/workflows/release.yaml")]
    public void Recognizes_ci_configurations(string file) =>
        ImportantFilesChecker.Check([file], false, null, NoEcosystems).Single(f => f.Kind == ImportantFileKind.CiWorkflow).Check.Present.Should().BeTrue();

    [Fact]
    public void Lockfiles_are_checked_only_for_package_managers_in_use()
    {
        var findings = ImportantFilesChecker.Check(["package.json", "pnpm-lock.yaml", "Cargo.toml", "go.mod", "Gemfile"], false, null, NoEcosystems);

        findings.Where(f => f.Kind == ImportantFileKind.Lockfile)
            .Select(f => (f.Check.Label, f.Check.Present, f.Check.Path, f.Ecosystem))
            .Should().Equal(
                ("Lockfile (npm)", true, "pnpm-lock.yaml", "npm"),
                ("Lockfile (Cargo)", false, null, "Cargo"),
                ("Lockfile (Bundler)", false, null, "Bundler"));
    }

    [Fact]
    public void A_go_module_needs_go_sum_only_when_it_has_dependencies()
    {
        ImportantFilesChecker.Check(["go.mod"], false, null, NoEcosystems).Should().NotContain(f => f.Kind == ImportantFileKind.Lockfile);
        ImportantFilesChecker.Check(["go.mod"], false, null, new HashSet<string> { "Go" })
            .Should().ContainSingle(f => f.Kind == ImportantFileKind.Lockfile && !f.Check.Present);
    }
}
