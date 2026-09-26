using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Detection.Ecosystems;

namespace ForgeDesk.Core.Tests.Detection;

/// <summary>Make, CMake, just and Task.</summary>
public class TaskRunnerDetectorTests
{
    private const string Makefile = """
        .PHONY: all build test clean help
        CC := gcc
        VERSION ?= 1.0
        SOURCES = $(wildcard src/*.c)

        all: build ## Build everything

        # Compile the binary
        build: $(SOURCES)
        	$(CC) -o app $(SOURCES)

        test: build
        	./run-tests.sh

        clean:
        	rm -rf out

        %.o: %.c
        	$(CC) -c $<

        out/app.exe: build

        _internal:
        	echo hidden

        lint fmt: ## Static checks
        	echo lint

        install::
        	cp app /usr/local/bin

        define TEMPLATE
        fake: target
        endef
        """;

    [Fact]
    public void Make_targets_skip_special_pattern_variable_and_file_targets()
    {
        var targets = MakeDetector.Targets(Makefile);

        targets.Select(t => t.Target).Should().Equal("all", "build", "test", "clean", "lint", "fmt", "install");
        targets.Single(t => t.Target == "all").Description.Should().Be("Build everything");
        targets.Single(t => t.Target == "build").Description.Should().Be("Compile the binary");
        targets.Single(t => t.Target == "test").Description.Should().BeNull();
    }

    [Fact]
    public async Task Makefile_targets_become_make_commands()
    {
        using var fixture = new DetectionFixture().With("Makefile", Makefile.ReplaceLineEndings("\r\n"));

        var profile = await fixture.DetectAsync();

        profile.Command("make:build").Should().Match<DetectedCommand>(c => c.CommandLine == "make build" && c.Category == CommandCategory.Build && c.Source == "Makefile");
        profile.Command("make:test").Category.Should().Be(CommandCategory.Test);
        profile.Command("make:clean").Category.Should().Be(CommandCategory.Clean);
        profile.Command("make:fmt").Category.Should().Be(CommandCategory.Format);
        profile.Command("make:install").Category.Should().Be(CommandCategory.Install);
        profile.BuildSystems.Should().Contain("Make");
    }

    [Fact]
    public async Task Makefile_targets_are_capped()
    {
        var text = string.Join('\n', Enumerable.Range(0, 50).Select(i => $"target{i}:\n\techo {i}"));
        using var fixture = new DetectionFixture().With("Makefile", text);

        var profile = await fixture.DetectAsync();

        profile.Commands.Count(c => c.Id.StartsWith("make:", StringComparison.Ordinal)).Should().Be(MakeDetector.MaxTargets);
    }

    [Fact]
    public async Task CMake_configure_build_and_ctest()
    {
        using var fixture = new DetectionFixture()
            .With("CMakeLists.txt", "cmake_minimum_required(VERSION 3.25)\nproject(engine LANGUAGES CXX)\nenable_testing()\nfind_package(GTest REQUIRED)\nadd_test(NAME unit COMMAND unit)\n")
            .With("src/engine.cpp", string.Concat(Enumerable.Repeat("int add(int a, int b) { return a + b; }\n", 20)));

        var profile = await fixture.DetectAsync();

        profile.Command("cmake:configure").Should().Match<DetectedCommand>(c => c.CommandLine == "cmake -S . -B build" && c.Category == CommandCategory.Build);
        profile.Command("cmake:build").CommandLine.Should().Be("cmake --build build");
        profile.Command("cmake:test").Should().Match<DetectedCommand>(c => c.CommandLine == "ctest --test-dir build" && c.Category == CommandCategory.Test);
        profile.Tests.Frameworks.Should().Contain(["CTest", "GoogleTest"]);
        profile.PrimaryLanguage.Should().Be("C++");
    }

    [Fact]
    public async Task CMake_without_tests_has_no_ctest_command()
    {
        using var fixture = new DetectionFixture().With("CMakeLists.txt", "project(tool C)\nadd_executable(tool main.c)\n");

        var profile = await fixture.DetectAsync();

        profile.HasCommand("cmake:test").Should().BeFalse();
    }

    [Fact]
    public void Just_recipes_skip_private_parameterized_and_settings()
    {
        const string justfile = """
            set shell := ["powershell.exe", "-c"]
            version := "1.2.3"
            alias b := build

            # Build the app
            build mode='debug':
                cargo build

            [doc('Run the tests')]
            test *args:
                cargo test {{args}}

            deploy env:
                ./deploy.sh {{env}}

            [private]
            helper:
                echo hidden

            _secret:
                echo hidden

            @fmt:
                cargo fmt
            """;

        var recipes = JustDetector.Recipes(justfile);

        recipes.Select(r => r.Recipe).Should().Equal("build", "test", "fmt");
        recipes[0].Description.Should().Be("Build the app");
        recipes[1].Description.Should().Be("Run the tests");
    }

    [Fact]
    public async Task Justfile_recipes_become_just_commands()
    {
        using var fixture = new DetectionFixture().With("justfile", "dev:\n    npm run dev\n\nlint:\n    eslint .\n");

        var profile = await fixture.DetectAsync();

        profile.Command("just:dev").Should().Match<DetectedCommand>(c => c.CommandLine == "just dev" && c.Category == CommandCategory.Dev);
        profile.Command("just:lint").Category.Should().Be(CommandCategory.Lint);
    }

    [Fact]
    public async Task Taskfile_tasks_skip_internal_ones()
    {
        using var fixture = new DetectionFixture().With("Taskfile.yml", """
            version: '3'

            vars:
              BINARY: app

            tasks:
              build:
                desc: Build the binary
                cmds:
                  - go build -o {{.BINARY}}
              "docker:build":
                cmds:
                  - docker build .
              test:unit:
                summary: |
                  Runs the unit tests.
                  build: not a task
                cmds:
                  - go test ./...
              setup-internal:
                internal: true
                cmds:
                  - echo hidden
            """);

        var profile = await fixture.DetectAsync();

        profile.Command("task:build").Should().Match<DetectedCommand>(c => c.CommandLine == "task build" && c.Category == CommandCategory.Build && c.Description == "Build the binary");
        profile.Command("task:docker:build").Should().Match<DetectedCommand>(c => c.CommandLine == "task docker:build" && c.Category == CommandCategory.Build);
        profile.Command("task:test:unit").Category.Should().Be(CommandCategory.Test);
        profile.HasCommand("task:setup-internal").Should().BeFalse();
        profile.Commands.Count(c => c.Id.StartsWith("task:", StringComparison.Ordinal)).Should().Be(3);
        profile.BuildSystems.Should().Contain("Task");
    }
}
