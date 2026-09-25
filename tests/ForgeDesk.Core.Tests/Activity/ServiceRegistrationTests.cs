using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Releases;
using ForgeDesk.Core.Storage;
using ForgeDesk.Core.Tests.Infrastructure;
using ForgeDesk.Core.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Activity;

public class ServiceRegistrationTests
{
    [Fact]
    public async Task Domain_services_resolve_as_singletons()
    {
        using var db = await TestDatabase.CreateAsync();
        var services = new ServiceCollection()
            .AddSingleton(db.Database)
            .AddSingleton<IClock>(SystemClock.Instance)
            .AddSingleton(Substitute.For<IGitService>())
            .AddSingleton(Substitute.For<IGitHubService>())
            .AddSingleton(Substitute.For<IProjectRegistry>())
            .AddSingleton(Substitute.For<IProjectDetector>())
            .AddActivityServices()
            .AddWorkItemsServices()
            .AddAnalysisServices()
            .AddReleasesServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IActivityLog>().Should().BeOfType<ActivityLog>().And.BeSameAs(provider.GetRequiredService<IActivityLog>());
        provider.GetRequiredService<IWorkItemService>().Should().BeOfType<WorkItemService>();
        provider.GetRequiredService<IProjectAnalyzer>().Should().BeOfType<ProjectAnalyzer>();
        provider.GetRequiredService<IReleaseService>().Should().BeOfType<ReleaseService>();
    }

    [Fact]
    public void Registrations_do_not_override_host_replacements()
    {
        var custom = Substitute.For<IActivityLog>();
        var services = new ServiceCollection().AddSingleton(custom).AddActivityServices();

        services.Where(d => d.ServiceType == typeof(IActivityLog)).Should().ContainSingle().Which.ImplementationInstance.Should().BeSameAs(custom);
    }
}
