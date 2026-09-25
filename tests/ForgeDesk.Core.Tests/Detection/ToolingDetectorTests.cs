using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Detection.Ecosystems;

namespace ForgeDesk.Core.Tests.Detection;

/// <summary>Deno, Dart/Flutter, Docker, root build scripts and VS Code tasks.</summary>
public class ToolingDetectorTests
{
    [Fact]
    public async Task Deno_tasks_from_jsonc_with_comments()
    {
        using var fixture = new DetectionFixture()
            .With("deno.jsonc", """
                {
                  // Development tasks
                  "tasks": {
                    "dev": "deno run --watch main.ts",
                    "build": { "command": "deno compile main.ts", "description": "Single binary" },
                  },
                }
                """)
            .With("main_test.ts", "Deno.test('x', () => {});");

        var profile = await fixture.DetectAsync();

        profile.Command("deno:dev").Should().Match<DetectedCommand>(c => c.CommandLine == "deno task dev" && c.Category == CommandCategory.Dev);
        profile.Command("deno:build").Should().Match<DetectedCommand>(c => c.Category == CommandCategory.Build && c.Description == "Single binary");
        profile.Command("deno:test").CommandLine.Should().Be("deno test");
        profile.Technology("Deno").Kind.Should().Be(TechnologyKind.Runtime);
        profile.Tests.Frameworks.Should().Contain("Deno test");
    }

    [Fact]
    public async Task Flutter_app_with_platform_builds()
    {
        using var fixture = new DetectionFixture()
            .With("pubspec.yaml", """
                name: notes
                environment:
                  sdk: ^3.5.0
                dependencies:
                  flutter:
                    sdk: flutter
                  provider: ^6.1.0
                dev_dependencies:
                  flutter_test:
                    sdk: flutter
                flutter:
                  uses-material-design: true
                """)
            .With("lib/main.dart", "void main() {}")
            .With("test/widget_test.dart", "")
            .With("windows/runner/main.cpp", "")
            .With("android/app/build.gradle", "");

        var profile = await fixture.DetectAsync();

        profile.Command("flutter:run").Should().Match<DetectedCommand>(c => c.CommandLine == "flutter run" && c.Category == CommandCategory.Dev);
        profile.Command("flutter:test").Category.Should().Be(CommandCategory.Test);
        profile.Command("flutter:pub-get").Category.Should().Be(CommandCategory.Install);
        profile.Command("flutter:analyze").Category.Should().Be(CommandCategory.Lint);
        profile.Command("flutter:build-windows").Should().Match<DetectedCommand>(c => c.CommandLine == "flutter build windows" && c.Category == CommandCategory.Package);
        profile.Command("flutter:build-apk").CommandLine.Should().Be("flutter build apk");
        profile.HasCommand("flutter:build-web").Should().BeFalse();
        profile.Command("dart:format").CommandLine.Should().Be("dart format .");
        profile.Technology("Flutter").Kind.Should().Be(TechnologyKind.Framework);
        profile.Tests.Frameworks.Should().Contain("flutter_test");
    }

    [Fact]
    public async Task Plain_dart_package()
    {
        using var fixture = new DetectionFixture()
            .With("pubspec.yaml", "name: cli\ndev_dependencies:\n  test: ^1.25.0\n")
            .With("bin/cli.dart", "void main() {}");

        var profile = await fixture.DetectAsync();

        profile.Command("dart:run").CommandLine.Should().Be("dart run");
        profile.Command("dart:test").CommandLine.Should().Be("dart test");
        profile.Command("dart:pub-get").CommandLine.Should().Be("dart pub get");
        profile.HasCommand("flutter:run").Should().BeFalse();
    }

    [Fact]
    public async Task Dockerfile_and_compose_commands()
    {
        using var fixture = new DetectionFixture()
            .With("Dockerfile", "FROM alpine\n")
            .With("compose.yaml", "services:\n  web:\n    build: .\n");

        var profile = await fixture.DetectAsync();

        var image = DockerDetector.ImageName(Path.GetFileName(fixture.Root));
        profile.Command("docker:build").Should().Match<DetectedCommand>(c => c.CommandLine == $"docker build -t {image} ." && c.Category == CommandCategory.Package);
        profile.Command("compose:up").Should().Match<DetectedCommand>(c => c.CommandLine == "docker compose up" && c.Category == CommandCategory.Run);
        profile.Command("compose:down").CommandLine.Should().Be("docker compose down");
        profile.Command("compose:build").Category.Should().Be(CommandCategory.Build);
        profile.Technology("Docker").Kind.Should().Be(TechnologyKind.Infrastructure);
        profile.Technology("Docker Compose");
    }

    [Theory]
    [InlineData("My Project", "my-project")]
    [InlineData("ForgeDesk", "forgedesk")]
    [InlineData("__weird__", "weird")]
    [InlineData("日本", "app")]
    public void Docker_image_names_are_sanitized(string folder, string expected) =>
        DockerDetector.ImageName(folder).Should().Be(expected);

    [Fact]
    public async Task Root_build_scripts_become_commands()
    {
        using var fixture = new DetectionFixture()
            .With("build.ps1", "Write-Host build")
            .With("test-all.ps1", "Write-Host test")
            .With("build.cmd", "@echo off")
            .With("notes.ps1", "Write-Host not a task")
            .With("tools/build.ps1", "Write-Host nested");

        var profile = await fixture.DetectAsync();

        var build = profile.Command("script:build.ps1");
        build.CommandLine.Should().EndWith("-File build.ps1").And.Contain("-NoProfile");
        build.Category.Should().Be(CommandCategory.Build);
        profile.Command("script:test-all.ps1").Category.Should().Be(CommandCategory.Test);
        profile.HasCommand("script:notes.ps1").Should().BeFalse();
        profile.HasCommand("script:tools/build.ps1").Should().BeFalse();
        profile.HasCommand("script:build.cmd").Should().Be(OperatingSystem.IsWindows(), "batch files only run on Windows");
        if (OperatingSystem.IsWindows())
        {
            profile.Command("script:build.cmd").CommandLine.Should().Be("build.cmd");
        }
    }

    [Fact]
    public async Task Windows_PowerShell_is_used_when_pwsh_is_missing()
    {
        var detector = new BuildScriptDetector(_ => false);
        using var fixture = new DetectionFixture().With("setup.ps1", "");
        var context = new DetectionContext(fixture.Root, ["setup.ps1"], false);

        await detector.ContributeAsync(context, TestContext.Current.CancellationToken);

        var expected = OperatingSystem.IsWindows()
            ? "powershell -NoProfile -ExecutionPolicy Bypass -File setup.ps1"
            : "pwsh -NoProfile -File setup.ps1";
        context.Commands.Should().ContainSingle().Which.Should().Match<DetectedCommand>(c => c.CommandLine == expected && c.Category == CommandCategory.Install);
    }

    [Fact]
    public async Task VsCode_shell_tasks_with_comments_groups_and_variables()
    {
        using var fixture = new DetectionFixture().With(".vscode/tasks.json", """
            {
              // See https://go.microsoft.com/fwlink/?LinkId=733558
              "version": "2.0.0",
              "tasks": [
                {
                  "label": "Build solution",
                  "type": "shell",
                  "command": "dotnet",
                  "args": ["build", "${workspaceFolder}/App.sln", { "value": "-c Debug", "quoting": "strong" }],
                  "group": { "kind": "build", "isDefault": true },
                  "detail": "Debug build"
                },
                {
                  "label": "Unit tests",
                  "type": "process",
                  "command": "dotnet",
                  "args": ["test"],
                  "group": "test",
                  "options": { "cwd": "${workspaceFolder}/tests" }
                },
                {
                  "label": "Platform specific",
                  "type": "shell",
                  "command": "./serve.sh",
                  "windows": { "command": ".\\serve.cmd" },
                },
                { "label": "Current file", "type": "shell", "command": "node ${file}" },
                { "label": "Npm script", "type": "npm", "script": "build" },
                { "label": "Composite", "dependsOn": ["Build solution", "Unit tests"] },
                { "label": "Escape", "type": "shell", "command": "make", "options": { "cwd": "${workspaceFolder}/../other" } },
              ],
            }
            """);

        var profile = await fixture.DetectAsync();

        var build = profile.Command("vscode:Build solution");
        build.CommandLine.Should().Be("dotnet build App.sln \"-c Debug\"");
        build.Category.Should().Be(CommandCategory.Build);
        build.Description.Should().Be("Debug build");
        build.Source.Should().Be(".vscode/tasks.json");
        var tests = profile.Command("vscode:Unit tests");
        tests.Should().Match<DetectedCommand>(c => c.CommandLine == "dotnet test" && c.Category == CommandCategory.Test && c.WorkingDirectory == "tests");
        profile.Command("vscode:Platform specific").CommandLine.Should().Be(OperatingSystem.IsWindows() ? @".\serve.cmd" : "./serve.sh");
        profile.HasCommand("vscode:Current file").Should().BeFalse("it needs the file open in the editor");
        profile.HasCommand("vscode:Npm script").Should().BeFalse();
        profile.HasCommand("vscode:Composite").Should().BeFalse();
        profile.HasCommand("vscode:Escape").Should().BeFalse("its working directory is outside the project");
    }

    [Fact]
    public async Task VsCode_program_paths_use_platform_separators()
    {
        using var fixture = new DetectionFixture().With(".vscode/tasks.json", """
            { "tasks": [ { "label": "Generate", "type": "process", "command": "${workspaceFolder}/tools/gen.exe", "args": ["--out", "src/gen"] } ] }
            """);

        var profile = await fixture.DetectAsync();

        var expected = OperatingSystem.IsWindows() ? @"tools\gen.exe --out src/gen" : "tools/gen.exe --out src/gen";
        profile.Command("vscode:Generate").CommandLine.Should().Be(expected);
    }

    [Fact]
    public async Task Broken_tasks_json_is_tolerated()
    {
        using var fixture = new DetectionFixture().With(".vscode/tasks.json", "{ \"tasks\": [ { \"label\": ");

        var profile = await fixture.DetectAsync();

        profile.Notes.Should().Contain(n => n.Contains(".vscode/tasks.json", StringComparison.Ordinal));
        profile.Commands.Should().NotContain(c => c.Id.StartsWith("vscode:", StringComparison.Ordinal));
    }
}
