using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Settings;
using ForgeDesk.Core.Storage;
using ForgeDesk.Core.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Runs;

public class RunsRegistrationTests
{
    [Fact]
    public async Task Run_service_resolves_as_a_singleton()
    {
        using var db = await TestDatabase.CreateAsync();
        var services = new ServiceCollection()
            .AddSingleton<IAppPaths>(db.Paths)
            .AddSingleton(db.Database)
            .AddSingleton<IClock>(SystemClock.Instance)
            .AddSingleton(Substitute.For<ISettingsService>())
            .AddSingleton(Substitute.For<IActivityLog>())
            .AddRunsServices();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        provider.GetRequiredService<IRunService>().Should().BeOfType<RunService>()
            .And.BeSameAs(provider.GetRequiredService<IRunService>());
    }
}
