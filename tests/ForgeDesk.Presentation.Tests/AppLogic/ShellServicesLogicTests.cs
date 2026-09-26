using System.Globalization;
using ForgeDesk.App.Services.Security;
using ForgeDesk.App.Services.Windowing;
using ForgeDesk.App.Startup;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Storage;

namespace ForgeDesk.Presentation.Tests.AppLogic;

public class WindowPlacementCalculatorTests
{
    // A 1920×1080 primary monitor (taskbar at the bottom) and a second one on its right.
    private static readonly ScreenRect WorkArea = new(0, 0, 1920, 1040);
    private static readonly ScreenRect SingleScreen = new(0, 0, 1920, 1080);
    private static readonly ScreenRect TwoScreens = new(0, 0, 3840, 1080);

    [Fact]
    public void First_launch_centers_the_default_size()
    {
        var placement = WindowPlacementCalculator.Compute(null, SingleScreen, WorkArea);

        placement.Should().Be(new WindowPlacement(260, 70, 1400, 900, false));
    }

    [Fact]
    public void Default_size_is_clamped_to_small_work_areas()
    {
        var placement = WindowPlacementCalculator.Compute(null, new ScreenRect(0, 0, 1366, 768), new ScreenRect(0, 0, 1366, 728));

        placement.Should().Be(new WindowPlacement(0, 0, 1366, 728, false));
    }

    [Fact]
    public void Saved_placement_on_a_connected_monitor_is_kept()
    {
        var saved = new WindowPlacement(2100, 80, 1500, 900, false);

        WindowPlacementCalculator.Compute(saved, TwoScreens, WorkArea).Should().Be(saved);
    }

    [Fact]
    public void Placement_on_a_disconnected_monitor_moves_back_to_the_primary()
    {
        var saved = new WindowPlacement(2100, 80, 1500, 900, false);

        var placement = WindowPlacementCalculator.Compute(saved, SingleScreen, WorkArea);

        placement.Should().Be(WindowPlacementCalculator.Centered(WorkArea));
    }

    [Fact]
    public void Maximized_state_survives_a_monitor_change()
    {
        var saved = new WindowPlacement(2100, 80, 1500, 900, true);

        WindowPlacementCalculator.Compute(saved, SingleScreen, WorkArea).Maximized.Should().BeTrue();
    }

    [Fact]
    public void Window_hanging_off_an_edge_is_kept_while_its_title_bar_is_reachable()
    {
        var saved = new WindowPlacement(1700, 500, 1400, 900, false);

        WindowPlacementCalculator.Compute(saved, SingleScreen, WorkArea).Should().Be(saved);
    }

    [Fact]
    public void Title_bar_above_the_screen_is_not_reachable()
    {
        var saved = new WindowPlacement(100, -500, 1400, 900, false);

        WindowPlacementCalculator.Compute(saved, SingleScreen, WorkArea).Should().Be(WindowPlacementCalculator.Centered(WorkArea));
    }

    [Fact]
    public void Oversized_placement_is_clamped_to_the_screens()
    {
        var saved = new WindowPlacement(0, 0, 9000, 5000, false);

        var placement = WindowPlacementCalculator.Compute(saved, SingleScreen, WorkArea);

        placement.Width.Should().Be(1920);
        placement.Height.Should().Be(1080);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1200, 800)]
    [InlineData(0, 0, 50, 800)]
    [InlineData(0, double.PositiveInfinity, 1200, 800)]
    public void Corrupt_placements_are_ignored(double left, double top, double width, double height) =>
        WindowPlacementCalculator.Compute(new WindowPlacement(left, top, width, height, false), SingleScreen, WorkArea)
            .Should().Be(WindowPlacementCalculator.Centered(WorkArea));
}

public class JumpListPlannerTests
{
    private static Project P(string name, bool pinned = false, int? openedDaysAgo = null, int order = 0) => new()
    {
        Id = name,
        Name = name,
        Path = "/p/" + name,
        IsPinned = pinned,
        SortOrder = order,
        LastOpenedAt = openedDaysAgo is { } days ? new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero).AddDays(-days) : null,
    };

    [Fact]
    public void Pinned_first_then_most_recent()
    {
        var plan = JumpListPlanner.Plan([P("old", openedDaysAgo: 9), P("pin-b", pinned: true, order: 2), P("new", openedDaysAgo: 1),
            P("never"), P("pin-a", pinned: true, order: 1)]);

        plan.Select(e => e.Project.Name).Should().Equal("pin-a", "pin-b", "new", "old");
        plan.Take(2).Should().OnlyContain(e => e.Category == JumpListPlanner.PinnedCategory);
        plan.Skip(2).Should().OnlyContain(e => e.Category == JumpListPlanner.RecentCategory);
    }

    [Fact]
    public void List_is_capped()
    {
        var projects = Enumerable.Range(0, 30).Select(i => P($"p{i:00}", pinned: i < 8, openedDaysAgo: i)).ToList();

        var plan = JumpListPlanner.Plan(projects);

        plan.Should().HaveCount(JumpListPlanner.MaxTotal);
        plan.Count(e => e.Category == JumpListPlanner.PinnedCategory).Should().Be(JumpListPlanner.MaxPinned);
    }

    [Fact]
    public void Nothing_opened_yet_gives_an_empty_list() => JumpListPlanner.Plan([P("a"), P("b")]).Should().BeEmpty();
}

public class CredentialTargetTests
{
    [Theory]
    [InlineData("github.com", "ForgeDesk:github.com")]
    [InlineData(" github.com ", "ForgeDesk:github.com")]
    [InlineData("ForgeDesk:github.com", "ForgeDesk:github.com")]
    [InlineData("forgedesk:github.com", "forgedesk:github.com")]
    public void Keys_are_prefixed_once(string key, string expected) => CredentialTarget.For(key).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_keys_are_rejected(string key) => FluentActions.Invoking(() => CredentialTarget.For(key)).Should().Throw<ArgumentException>();
}

public class StartupMessagesTests
{
    [Fact]
    public void Restored_backup_names_its_date()
    {
        var result = new DatabaseOpenResult(true, "/data/backups/corrupted-20260925-101010.db", "/data/backups/forgedesk-20260924-083000.db");

        var (title, message) = DatabaseRecoveryMessage.For(result, CultureInfo.InvariantCulture);

        title.Should().Be(DatabaseRecoveryMessage.RestoredTitle);
        message.Should().StartWith("ForgeDesk's local data was damaged and has been restored from the backup of ")
            .And.Contain("2026").And.Contain("08:30");
    }

    [Fact]
    public void Missing_backup_means_reset()
    {
        var (title, message) = DatabaseRecoveryMessage.For(new DatabaseOpenResult(true, "/q.db", null), CultureInfo.InvariantCulture);

        title.Should().Be(DatabaseRecoveryMessage.ResetTitle);
        message.Should().Be("ForgeDesk's local data was damaged and has been reset.");
    }

    [Fact]
    public void Unrecognized_backup_name_still_explains_the_restore() =>
        DatabaseRecoveryMessage.For(new DatabaseOpenResult(true, null, "/b/custom.db"), CultureInfo.InvariantCulture).Message
            .Should().Be("ForgeDesk's local data was damaged and has been restored from the latest backup.");

    [Fact]
    public void Crash_report_has_environment_and_stack()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        var at = new DateTimeOffset(2026, 9, 25, 14, 3, 7, TimeSpan.FromHours(2));
        var report = CrashReport.Format(exception, at, "1.0.0", ".NET 10.0.0", "Windows 11 (X64)");

        report.Should().ContainAll("ForgeDesk crash report", "1.0.0", ".NET 10.0.0", "Windows 11 (X64)", "InvalidOperationException", "boom",
            "2026-09-25T14:03:07.0000000+02:00", nameof(Crash_report_has_environment_and_stack));
        CrashReport.FileName(at).Should().Be("crash-20260925-140307.txt");
    }

    [Fact]
    public void Crash_report_is_written_to_disk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forgedesk-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = CrashReport.TryWrite(directory, new InvalidOperationException("disk test"));

            path.Should().NotBeNull();
            File.ReadAllText(path!).Should().Contain("disk test");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Version_has_no_build_metadata() => AppInfo.Version.Should().NotContain("+").And.NotBeNullOrWhiteSpace();
}
