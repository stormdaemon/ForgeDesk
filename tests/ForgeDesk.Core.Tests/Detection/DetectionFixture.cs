using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ForgeDesk.Core.Tests.Detection;

/// <summary>Builds project folders in a temp directory and runs the fully registered detector on them.</summary>
public sealed class DetectionFixture : IDisposable
{
    private readonly TempDirectory _directory = new("detect");

    public DetectionFixture()
    {
        Git = Substitute.For<IGitService>();
        var services = new ServiceCollection();
        services.AddDetectionServices();
        services.AddSingleton(Git);
        services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)));
        Provider = services.BuildServiceProvider();
    }

    public IGitService Git { get; }

    public ServiceProvider Provider { get; }

    public string Root => _directory.Path;

    public DetectionFixture With(string relativePath, string content = "")
    {
        _directory.WriteFile(relativePath, content);
        return this;
    }

    public DetectionFixture WithDirectory(string relativePath)
    {
        Directory.CreateDirectory(_directory.Combine(relativePath.Split('/')));
        return this;
    }

    public Task<ProjectProfile> DetectAsync() =>
        Provider.GetRequiredService<IProjectDetector>().DetectAsync(Root, TestContext.Current.CancellationToken);

    public void Dispose()
    {
        Provider.Dispose();
        _directory.Dispose();
    }
}

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

internal static class ProfileAssertions
{
    public static DetectedCommand Command(this ProjectProfile profile, string id)
    {
        var command = profile.Commands.FirstOrDefault(c => c.Id == id);
        command.Should().NotBeNull($"command '{id}' should be detected; found: {string.Join(", ", profile.Commands.Select(c => c.Id))}");
        return command!;
    }

    public static Technology Technology(this ProjectProfile profile, string name)
    {
        var technology = profile.Technologies.FirstOrDefault(t => t.Name == name);
        technology.Should().NotBeNull($"technology '{name}' should be detected; found: {string.Join(", ", profile.Technologies.Select(t => t.Name))}");
        return technology!;
    }

    public static bool HasCommand(this ProjectProfile profile, string id) => profile.Commands.Any(c => c.Id == id);
}
