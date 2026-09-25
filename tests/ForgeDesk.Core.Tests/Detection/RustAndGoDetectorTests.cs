using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Detection.Ecosystems;

namespace ForgeDesk.Core.Tests.Detection;

public class RustAndGoDetectorTests
{
    [Fact]
    public async Task Cargo_package_with_binary()
    {
        using var fixture = new DetectionFixture()
            .With("Cargo.toml", """
                [package]
                name = "server"
                version = "0.1.0"

                [dependencies]
                tokio = { version = "1", features = ["full"] }
                axum = "0.7"

                [dependencies.serde]
                version = "1"

                [dev-dependencies]
                criterion = "0.5"
                """)
            .With("src/main.rs", "fn main() {}")
            .With("tests/api.rs", "")
            .With("benches/speed.rs", "");

        var profile = await fixture.DetectAsync();

        profile.Command("cargo:build").Should().Match<DetectedCommand>(c => c.CommandLine == "cargo build" && c.Category == CommandCategory.Build);
        profile.Command("cargo:test").Category.Should().Be(CommandCategory.Test);
        profile.Command("cargo:run").Should().Match<DetectedCommand>(c => c.CommandLine == "cargo run" && c.Category == CommandCategory.Run);
        profile.Command("cargo:clippy").Category.Should().Be(CommandCategory.Lint);
        profile.Command("cargo:check").Category.Should().Be(CommandCategory.Lint);
        profile.Command("cargo:fmt").Should().Match<DetectedCommand>(c => c.CommandLine == "cargo fmt" && c.Category == CommandCategory.Format);
        profile.Command("cargo:bench").CommandLine.Should().Be("cargo bench");
        profile.Command("cargo:build-release").Category.Should().Be(CommandCategory.Package);
        profile.Technology("Tokio");
        profile.Technology("Axum").Kind.Should().Be(TechnologyKind.Framework);
        profile.Technology("Rust").Kind.Should().Be(TechnologyKind.Language);
        profile.BuildSystems.Should().Contain("Cargo");
        profile.Tests.Locations.Should().Contain("tests");
        profile.IsMonorepo.Should().BeFalse();
    }

    [Fact]
    public async Task Cargo_workspace_is_a_monorepo_with_member_binaries()
    {
        using var fixture = new DetectionFixture()
            .With("Cargo.toml", """
                [workspace]
                members = [
                    "crates/app",   # the desktop app
                    "crates/core",
                ]
                """)
            .With("crates/app/Cargo.toml", "[package]\nname = \"desk-app\"\n\n[dependencies]\ntauri = \"2\"\n")
            .With("crates/app/src/main.rs", "fn main() {}")
            .With("crates/core/Cargo.toml", "[package]\nname = \"desk-core\"\n[dependencies]\nactix-web = \"4\"\n")
            .With("crates/core/src/lib.rs", "");

        var profile = await fixture.DetectAsync();

        profile.IsMonorepo.Should().BeTrue();
        profile.Command("cargo:build").CommandLine.Should().Be("cargo build --workspace");
        profile.Command("cargo:fmt").CommandLine.Should().Be("cargo fmt --all");
        profile.Command("cargo:run:desk-app").CommandLine.Should().Be("cargo run -p desk-app");
        profile.HasCommand("cargo:run:desk-core").Should().BeFalse();
        profile.HasCommand("cargo:run").Should().BeFalse();
        profile.Technology("Tauri");
        profile.Technology("Actix Web");
    }

    [Fact]
    public async Task Go_module_with_main_package_and_commands()
    {
        using var fixture = new DetectionFixture()
            .With("go.mod", """
                module github.com/acme/svc

                go 1.23

                require (
                    github.com/gin-gonic/gin v1.10.0 // indirect
                    github.com/spf13/cobra v1.8.1
                )

                require github.com/labstack/echo/v4 v4.12.0
                """)
            .With("main.go", "// Command svc.\npackage main\n\nfunc main() {}\n")
            .With("cmd/worker/main.go", "package main\nfunc main() {}\n")
            .With("internal/store/store_test.go", "package store\n");

        var profile = await fixture.DetectAsync();

        profile.Command("go:build").Should().Match<DetectedCommand>(c => c.CommandLine == "go build ./..." && c.Category == CommandCategory.Build);
        profile.Command("go:test").CommandLine.Should().Be("go test ./...");
        profile.Command("go:vet").Should().Match<DetectedCommand>(c => c.CommandLine == "go vet ./..." && c.Category == CommandCategory.Lint);
        profile.Command("go:fmt").Should().Match<DetectedCommand>(c => c.CommandLine == "gofmt -l ." && c.Category == CommandCategory.Format);
        profile.Command("go:run").CommandLine.Should().Be("go run .");
        profile.Command("go:run:worker").CommandLine.Should().Be("go run ./cmd/worker");
        profile.Technology("Gin");
        profile.Technology("Echo");
        profile.Technology("Cobra");
        profile.Tests.Frameworks.Should().Contain("go test");
    }

    [Fact]
    public async Task Go_library_has_no_run_command()
    {
        using var fixture = new DetectionFixture()
            .With("go.mod", "module example.com/lib\n\ngo 1.22\n")
            .With("lib.go", "package lib\n");

        var profile = await fixture.DetectAsync();

        profile.HasCommand("go:run").Should().BeFalse();
        profile.HasCommand("go:build").Should().BeTrue();
    }

    [Fact]
    public async Task Go_workspace_is_a_monorepo()
    {
        using var fixture = new DetectionFixture().With("go.work", "go 1.23\n\nuse (\n  ./api\n  ./cli\n)\n");

        var profile = await fixture.DetectAsync();

        profile.IsMonorepo.Should().BeTrue();
    }

    [Fact]
    public void Go_mod_requirements_are_parsed_in_both_forms()
    {
        var modules = GoDetector.RequiredModules("module x\r\nrequire a.com/one v1\r\nrequire (\r\n\tb.com/two v2 // indirect\r\n\r\n\tc.com/three v3\r\n)\r\n");

        modules.Should().Equal("a.com/one", "b.com/two", "c.com/three");
    }
}
