using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Storage;

public class SettingsServiceTests
{
    [Fact]
    public async Task Settings_round_trip_and_raise_changed()
    {
        using var db = await TestDatabase.CreateAsync();
        var service = new SettingsService(db.Database);
        AppSettings? observed = null;
        service.Changed += (_, s) => observed = s;

        await service.UpdateAsync(s => s with { Theme = ThemePreference.Light, AutoFetchIntervalMinutes = 3 }, TestContext.Current.CancellationToken);

        observed!.Theme.Should().Be(ThemePreference.Light);
        var reloaded = new SettingsService(db.Database);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        reloaded.Current.Theme.Should().Be(ThemePreference.Light);
        reloaded.Current.AutoFetchIntervalMinutes.Should().Be(3);
    }

    [Fact]
    public async Task Unreadable_settings_fall_back_to_defaults()
    {
        using var db = await TestDatabase.CreateAsync();
        await db.Database.UseAsync(c => Dapper.SqlMapper.ExecuteAsync(c, "INSERT INTO settings(key, value_json) VALUES ('app', '{not json')"), TestContext.Current.CancellationToken);

        var service = new SettingsService(db.Database);
        await service.LoadAsync(TestContext.Current.CancellationToken);

        service.Current.Should().Be(AppSettings.Default);
    }
}
