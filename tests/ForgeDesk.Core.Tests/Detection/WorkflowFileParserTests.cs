using ForgeDesk.Core.Detection;

namespace ForgeDesk.Core.Tests.Detection;

public class WorkflowFileParserTests
{
    private const string Path = ".github/workflows/ci.yml";

    [Fact]
    public void Scalar_trigger()
    {
        var workflow = WorkflowFileParser.Parse(Path, "name: CI\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n");

        workflow.Name.Should().Be("CI");
        workflow.RelativePath.Should().Be(Path);
        workflow.Triggers.Should().Equal("push");
    }

    [Fact]
    public void Flow_list_trigger_with_quotes()
    {
        var workflow = WorkflowFileParser.Parse(Path, "name: 'Build & test'\non: [push, \"pull_request\", workflow_dispatch]\n");

        workflow.Name.Should().Be("Build & test");
        workflow.Triggers.Should().Equal("push", "pull_request", "workflow_dispatch");
    }

    [Fact]
    public void Block_list_trigger()
    {
        var workflow = WorkflowFileParser.Parse(Path, "on:\n  - push\n  - pull_request # PRs too\njobs: {}\n");

        workflow.Triggers.Should().Equal("push", "pull_request");
    }

    [Fact]
    public void Map_trigger_with_nested_filters()
    {
        const string yaml = """
            name: Release
            "on":
              push:
                tags:
                  - 'v*'
                branches: [main]
              schedule:
                - cron: '0 3 * * 1'
              workflow_dispatch:
                inputs:
                  dry-run:
                    type: boolean
            permissions:
              contents: write
            jobs:
              release:
                runs-on: windows-latest
                steps:
                  - run: |
                      echo on: not a trigger
                      pull_request:
            """;

        var workflow = WorkflowFileParser.Parse(Path, yaml);

        workflow.Name.Should().Be("Release");
        workflow.Triggers.Should().Equal("push", "schedule", "workflow_dispatch");
    }

    [Fact]
    public void Flow_map_trigger_spanning_lines()
    {
        var workflow = WorkflowFileParser.Parse(Path, "on: { push: { branches: [main, 'release/*'] },\n      pull_request: {} }\njobs: {}\n");

        workflow.Triggers.Should().Equal("push", "pull_request");
    }

    [Fact]
    public void Crlf_comments_and_missing_name_fall_back_to_file_name()
    {
        var workflow = WorkflowFileParser.Parse(".github/workflows/nightly-build.yaml", "# Nightly\r\non: # when\r\n  schedule:\r\n    - cron: '0 0 * * *'\r\n");

        workflow.Name.Should().Be("nightly-build");
        workflow.Triggers.Should().Equal("schedule");
    }

    [Fact]
    public void Workflow_without_trigger_has_no_triggers()
    {
        var workflow = WorkflowFileParser.Parse(Path, "name: Broken\njobs: {}\n");

        workflow.Triggers.Should().BeEmpty();
    }

    [Fact]
    public async Task Detector_reads_every_workflow_file()
    {
        using var fixture = new DetectionFixture()
            .With(".github/workflows/ci.yml", "name: CI\non: [push, pull_request]\n")
            .With(".github/workflows/release.yaml", "name: Release\non:\n  push:\n    tags: ['v*']\n")
            .With(".github/workflows/README.md", "not a workflow")
            .With(".github/dependabot.yml", "version: 2\n");

        var profile = await fixture.DetectAsync();

        profile.Workflows.Select(w => w.Name).Should().Equal("CI", "Release");
        profile.Workflows[1].Triggers.Should().Equal("push");
        profile.ImportantFiles.Should().Contain(".github/workflows");
    }
}
