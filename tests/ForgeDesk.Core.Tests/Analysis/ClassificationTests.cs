using ForgeDesk.Core.Analysis;

namespace ForgeDesk.Core.Tests.Analysis;

public class ClassificationTests
{
    [Theory]
    [InlineData("src/Program.cs", "C#")]
    [InlineData("SRC/PROGRAM.CS", "C#")]
    [InlineData(@"src\App.xaml", "XAML")]
    [InlineData("web/app.component.tsx", "TypeScript")]
    [InlineData("types/index.d.ts", "TypeScript")]
    [InlineData("main.rs", "Rust")]
    [InlineData("build.gradle", "Groovy")]
    [InlineData("scripts/deploy.ps1", "PowerShell")]
    [InlineData("build.cmd", "Batchfile")]
    [InlineData("Dockerfile", "Dockerfile")]
    [InlineData("docker/Dockerfile.dev", "Dockerfile")]
    [InlineData("Makefile", "Makefile")]
    [InlineData("CMakeLists.txt", "CMake")]
    [InlineData("Gemfile", "Ruby")]
    [InlineData("Jenkinsfile", "Groovy")]
    [InlineData("src/App.vue", "Vue")]
    [InlineData("Views/Index.cshtml", "Razor")]
    public void Detects_languages_by_extension_or_file_name(string path, string language) =>
        LanguageCatalog.Detect(path)!.Name.Should().Be(language);

    [Theory]
    [InlineData("package.json")]
    [InlineData("README.md")]
    [InlineData("config.yaml")]
    [InlineData(".bashrc")]
    [InlineData("LICENSE")]
    [InlineData("image.png")]
    [InlineData("notes.txt")]
    public void Data_prose_and_unknown_files_are_not_languages(string path) =>
        LanguageCatalog.Detect(path).Should().BeNull();

    [Fact]
    public void Every_language_has_a_hex_color() =>
        new[] { "a.cs", "a.ts", "a.py", "a.go", "a.rs", "a.java", "a.kt", "a.swift", "a.rb", "a.php", "a.cpp", "a.c", "a.html", "a.css", "a.sh" }
            .Select(LanguageCatalog.Detect)
            .Should().OnlyContain(l => l != null && System.Text.RegularExpressions.Regex.IsMatch(l.Color, "^#[0-9A-Fa-f]{6}$"));

    [Theory]
    [InlineData("tests/ForgeDesk.Core.Tests/Git/GitServiceTests.cs")]
    [InlineData("src/Parser.Tests/ParserTests.cs")]
    [InlineData("src/app.test.ts")]
    [InlineData("src/app.spec.js")]
    [InlineData("web/__tests__/button.jsx")]
    [InlineData("pkg/parser_test.go")]
    [InlineData("tests/test_utils.py")]
    [InlineData("app/test_models.py")]
    [InlineData("spec/models/user_spec.rb")]
    [InlineData("src/main/java/com/x/ParserTest.java")]
    [InlineData("src/test/java/com/x/Anything.java")]
    [InlineData("src/it/ParserIT.java")]
    [InlineData("core/src/ParserSpec.scala")]
    [InlineData("app/src/androidTest/java/MainActivityTest.kt")]
    public void Recognizes_test_files(string path) => TestFileClassifier.IsTestFile(path).Should().BeTrue();

    [Theory]
    [InlineData("src/Latest.cs")]
    [InlineData("src/Contest.java")]
    [InlineData("src/testing-utils.md")]
    [InlineData("tests/fixtures/data.json")]
    [InlineData("src/attestation.ts")]
    [InlineData("src/Test.cs")]
    public void Ignores_non_test_files(string path) => TestFileClassifier.IsTestFile(path).Should().BeFalse();
}
