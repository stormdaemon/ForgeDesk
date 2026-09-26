using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using ForgeDesk.Core.Settings;

namespace ForgeDesk.UITests;

/// <summary>
/// End-to-end smoke tests of the packaged app. They assert that the main journeys work without
/// crashing and capture screenshots of every screen for visual review (CI uploads them).
/// </summary>
[Collection("app")]
public sealed class SmokeTests
{
    private static readonly VirtualKeyShort[] Digits =
    [
        VirtualKeyShort.KEY_1, VirtualKeyShort.KEY_2, VirtualKeyShort.KEY_3, VirtualKeyShort.KEY_4, VirtualKeyShort.KEY_5,
        VirtualKeyShort.KEY_6, VirtualKeyShort.KEY_7, VirtualKeyShort.KEY_8, VirtualKeyShort.KEY_9, VirtualKeyShort.KEY_0,
    ];

    private static void RequireApp() =>
        Assert.SkipUnless(OperatingSystem.IsWindows() && AppSession.AppPath is not null, "Set FORGEDESK_APP_PATH to a published ForgeDesk.exe (Windows only).");

    [Fact]
    public void First_launch_shows_a_usable_window()
    {
        RequireApp();
        using var session = AppSession.Launch(DataFolder.Create("first"));
        AppSession.Settle(4);
        session.Screenshot("01-first-launch");

        session.HasExited.Should().BeFalse();
        session.MainWindow.Title.Should().Contain("ForgeDesk");
    }

    [Fact]
    public async Task Opening_a_repository_shows_every_workspace_tab()
    {
        RequireApp();
        using var project = new SampleProject();
        var data = await DataFolder.CreateWithSettingsAsync("workspace", s => s with { OnboardingCompleted = true, Theme = ThemePreference.Dark });

        using var session = AppSession.Launch(data, project.Root);
        AppSession.Settle(5);
        session.Screenshot("10-workspace-dark");

        string[] names = ["overview", "git", "files", "terminal", "commands", "tasks", "github", "releases", "insights", "activity"];
        for (var i = 0; i < names.Length; i++)
        {
            session.Press(VirtualKeyShort.CONTROL, Digits[i]);
            AppSession.Settle(i == 3 ? 4 : 2.5);
            session.Screenshot($"{11 + i}-tab-{names[i]}-dark");
            session.HasExited.Should().BeFalse($"ForgeDesk must survive opening the {names[i]} tab");
        }

        session.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        AppSession.Settle(1);
        session.Type("pu");
        AppSession.Settle(1.5);
        session.Screenshot("30-palette");
        session.Press(VirtualKeyShort.ESCAPE);

        session.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.OEM_COMMA);
        AppSession.Settle(2.5);
        session.Screenshot("31-settings");

        var home = session.FindByName("Home");
        home?.AsButton().Invoke();
        AppSession.Settle(3);
        session.Screenshot("32-dashboard");

        var activity = session.FindByName("Activity");
        activity?.AsButton().Invoke();
        AppSession.Settle(2.5);
        session.Screenshot("33-activity");

        session.HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task Light_theme_renders()
    {
        RequireApp();
        using var project = new SampleProject();
        var data = await DataFolder.CreateWithSettingsAsync("light", s => s with { OnboardingCompleted = true, Theme = ThemePreference.Light });

        using var session = AppSession.Launch(data, project.Root);
        AppSession.Settle(5);
        session.Screenshot("40-workspace-light");
        session.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_2);
        AppSession.Settle(3);
        session.Screenshot("41-git-light");
        session.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_3);
        AppSession.Settle(3);
        session.Screenshot("42-files-light");
        session.HasExited.Should().BeFalse();
    }

    [Fact]
    public void Onboarding_is_shown_on_first_launch()
    {
        RequireApp();
        using var session = AppSession.Launch(DataFolder.Create("onboarding"));
        AppSession.Settle(4);
        session.Screenshot("50-onboarding");
        session.HasExited.Should().BeFalse();
    }
}
