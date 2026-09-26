using ForgeDesk.Core.Settings;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Settings;
using ForgeDesk.Presentation.Tests.Settings.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Settings;

public sealed class GeneralAndAppearanceSettingsTests : IDisposable
{
    private readonly SettingsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void General_toggles_are_saved_immediately()
    {
        var general = CreateGeneral();

        general.RestoreLastProject = false;
        general.ConfirmBeforeClosing = false;

        _harness.Settings.Current.RestoreLastProjectOnStartup.Should().BeFalse();
        _harness.Settings.Current.ConfirmBeforeClosingWithRunningTasks.Should().BeFalse();
    }

    [Fact]
    public async Task The_clone_folder_is_chosen_with_the_picker_and_can_go_back_to_the_default()
    {
        var folder = _harness.Folder.Combine("clones");
        _harness.Dialogs.PickFolderAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(folder);
        var general = CreateGeneral();
        general.CloneFolderText.Should().Be(CloneDestination.DefaultBaseFolder());
        general.ResetCloneFolderCommand.CanExecute(null).Should().BeFalse();

        await general.ChangeCloneFolderCommand.ExecuteAsync(null);

        _harness.Settings.Current.DefaultCloneDirectory.Should().Be(folder);
        general.CloneFolderText.Should().Be(folder);
        general.ResetCloneFolderCommand.CanExecute(null).Should().BeTrue();

        await general.ResetCloneFolderCommand.ExecuteAsync(null);

        _harness.Settings.Current.DefaultCloneDirectory.Should().BeNull();
        general.HasCustomCloneFolder.Should().BeFalse();
    }

    [Fact]
    public void Run_onboarding_again_opens_the_onboarding()
    {
        CreateGeneral().RunOnboardingCommand.Execute(null);

        _harness.Navigation.Received(1).OpenOnboarding();
    }

    [Fact]
    public void Choosing_a_theme_saves_it()
    {
        var appearance = new AppearanceSettingsViewModel(_harness.CreateStore());
        appearance.SelectedTheme.Value.Should().Be(ThemePreference.System);

        appearance.SelectedTheme = appearance.ThemeOptions.Single(o => o.IsDark);
        appearance.UseWindowsAccent = true;

        _harness.Settings.Current.Theme.Should().Be(ThemePreference.Dark);
        _harness.Settings.Current.UseSystemAccent.Should().BeTrue();
    }

    [Theory]
    [InlineData(14.0, null)]
    [InlineData(8.0, null)]
    [InlineData(32.0, null)]
    [InlineData(7.0, "Use a size between 8 and 32.")]
    [InlineData(40.0, "Use a size between 8 and 32.")]
    [InlineData(null, "Enter a size between 8 and 32.")]
    public void Font_sizes_are_validated_before_saving(double? size, string? error)
    {
        var appearance = new AppearanceSettingsViewModel(_harness.CreateStore());

        appearance.CodeFontSize = size;
        appearance.TerminalFontSize = size;

        appearance.CodeFontSizeError.Should().Be(error);
        appearance.TerminalFontSizeError.Should().Be(error);
        if (error is null)
        {
            _harness.Settings.Current.CodeFontSize.Should().Be(size!.Value);
            _harness.Settings.Current.TerminalFontSize.Should().Be(size.Value);
        }
        else
        {
            _harness.Settings.Current.CodeFontSize.Should().Be(AppSettings.Default.CodeFontSize);
            _harness.Settings.Current.TerminalFontSize.Should().Be(AppSettings.Default.TerminalFontSize);
        }
    }

    [Fact]
    public void An_empty_code_font_is_refused()
    {
        var appearance = new AppearanceSettingsViewModel(_harness.CreateStore());

        appearance.CodeFontFamily = "   ";

        appearance.HasCodeFontError.Should().BeTrue();
        _harness.Settings.Current.CodeFontFamily.Should().Be(AppSettings.Default.CodeFontFamily);

        appearance.CodeFontFamily = "JetBrains Mono, Consolas";

        appearance.HasCodeFontError.Should().BeFalse();
        _harness.Settings.Current.CodeFontFamily.Should().Be("JetBrains Mono, Consolas");
    }

    [Fact]
    public void A_failed_save_is_reported()
    {
        var appearance = new AppearanceSettingsViewModel(_harness.CreateStore());
        _harness.Settings.FailWith = new IOException("Disk full");

        appearance.UseWindowsAccent = true;

        _harness.Notifications.Received(1).ShowError(Arg.Is<Core.Common.ErrorInfo>(e => e.Title == "Could not save the setting"),
            Arg.Any<Infrastructure.NotificationAction?>());
    }

    private GeneralSettingsViewModel CreateGeneral() =>
        new(_harness.CreateStore(), _harness.Dialogs, _harness.Navigation, _harness.Notifications);
}
