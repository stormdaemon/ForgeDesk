using ForgeDesk.Core.Activity;
using ForgeDesk.Presentation.Activity;
using ForgeDesk.Presentation.Workspace;

namespace ForgeDesk.Presentation.Tests.Activity;

public sealed class ActivityTargetTests
{
    private static ActivityEntry Entry(string? refKind, string? refValue, string? projectId = "p1", ActivityKind kind = ActivityKind.GitCommit) =>
        ActivityHarness.Entry(1, FrozenTime.Now, "Entry", kind, projectId: projectId, refKind: refKind, refValue: refValue);

    [Theory]
    [InlineData("commit", "abc1234", WorkspaceSection.Git, "abc1234")]
    [InlineData("run", "run-1", WorkspaceSection.Commands, "run-1")]
    [InlineData("work-item", "wi-9", WorkspaceSection.Tasks, "wi-9")]
    [InlineData("branch", "feature/x", WorkspaceSection.Git, null)]
    [InlineData("release", "v1.0.0", WorkspaceSection.Releases, null)]
    public void References_lead_to_a_tab_with_a_plain_argument(string refKind, string refValue, WorkspaceSection section, string? argument)
    {
        var target = ActivityTarget.Resolve(Entry(refKind, refValue), isGlobal: false);

        target.Should().Be(new ActivityTarget(ActivityTargetKind.Section, "p1", section, argument));
    }

    [Fact]
    public void Web_urls_open_in_the_browser_and_other_schemes_are_ignored()
    {
        ActivityTarget.Resolve(Entry("url", "https://github.com/acme/app"), isGlobal: false)!.Url.Should().Be("https://github.com/acme/app");
        ActivityTarget.Resolve(Entry("url", "file:///c:/windows/system32/calc.exe"), isGlobal: false).Should().BeNull();
    }

    [Fact]
    public void On_the_global_page_entries_without_reference_open_their_project()
    {
        ActivityTarget.Resolve(Entry(null, null), isGlobal: true)
            .Should().Be(new ActivityTarget(ActivityTargetKind.Project, "p1"));
        ActivityTarget.Resolve(Entry(null, null), isGlobal: false).Should().BeNull();
        ActivityTarget.Resolve(Entry(null, null, kind: ActivityKind.ProjectRemoved), isGlobal: true).Should().BeNull();
        ActivityTarget.Resolve(Entry("commit", "abc1234", projectId: null), isGlobal: true).Should().BeNull();
    }

    [Fact]
    public void Day_labels_are_relative_for_the_last_week()
    {
        var today = new DateOnly(2026, 10, 7);

        ActivityDayHeaderViewModel.LabelFor(today, today).Should().Be("Today");
        ActivityDayHeaderViewModel.LabelFor(today.AddDays(-1), today).Should().Be("Yesterday");
        ActivityDayHeaderViewModel.LabelFor(today.AddDays(-6), today)
            .Should().Be(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(today.AddDays(-6).DayOfWeek));
        ActivityDayHeaderViewModel.LabelFor(new DateOnly(2024, 1, 2), today).Should().Contain("2024");
    }
}
