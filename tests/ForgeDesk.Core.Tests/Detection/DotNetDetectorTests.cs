using ForgeDesk.Core.Detection;

namespace ForgeDesk.Core.Tests.Detection;

public class DotNetDetectorTests
{
    private const string WebProject = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
          </ItemGroup>
        </Project>
        """;

    private const string TestProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="xunit.v3" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public async Task Solution_with_web_and_test_projects()
    {
        using var fixture = new DetectionFixture()
            .With("Shop.slnx", "<Solution />")
            .With("src/Shop.Api/Shop.Api.csproj", WebProject)
            .With("src/Shop.Core/Shop.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>")
            .With("tests/Shop.Tests/Shop.Tests.csproj", TestProject);

        var profile = await fixture.DetectAsync();

        profile.Command("dotnet:restore").Should().Match<DetectedCommand>(c => c.CommandLine == "dotnet restore Shop.slnx" && c.Category == CommandCategory.Install);
        profile.Command("dotnet:build").CommandLine.Should().Be("dotnet build Shop.slnx");
        profile.Command("dotnet:test").Should().Match<DetectedCommand>(c => c.CommandLine == "dotnet test Shop.slnx" && c.Category == CommandCategory.Test);
        profile.Command("dotnet:format").Category.Should().Be(CommandCategory.Format);
        profile.Command("dotnet:clean").Category.Should().Be(CommandCategory.Clean);
        profile.Command("dotnet:publish").Should().Match<DetectedCommand>(c => c.CommandLine == "dotnet publish src/Shop.Api/Shop.Api.csproj -c Release" && c.Category == CommandCategory.Package);
        profile.Command("dotnet:run:Shop.Api").Should().Match<DetectedCommand>(c => c.CommandLine == "dotnet run --project src/Shop.Api/Shop.Api.csproj" && c.Category == CommandCategory.Run);
        profile.HasCommand("dotnet:run:Shop.Tests").Should().BeFalse("test projects are not applications even with OutputType Exe");
        profile.HasCommand("dotnet:run:Shop.Core").Should().BeFalse("class libraries cannot run");

        profile.Technology("ASP.NET Core").Kind.Should().Be(TechnologyKind.Framework);
        profile.Technology("EF Core").Kind.Should().Be(TechnologyKind.Library);
        profile.Technology("xUnit").Kind.Should().Be(TechnologyKind.TestFramework);
        profile.Technology(".NET").Evidence.Should().Contain("net10.0");
        profile.Technology("C#").Kind.Should().Be(TechnologyKind.Language);
        profile.BuildSystems.Should().Contain(".NET SDK");
        profile.Tests.HasTests.Should().BeTrue();
        profile.Tests.Frameworks.Should().Contain("xUnit");
        profile.Tests.Locations.Should().Contain("tests/Shop.Tests");
    }

    [Fact]
    public async Task Classic_sln_is_preferred_when_both_solution_formats_exist()
    {
        using var fixture = new DetectionFixture()
            .With("App.sln", "")
            .With("App.slnx", "<Solution />")
            .With("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var profile = await fixture.DetectAsync();

        profile.Command("dotnet:build").CommandLine.Should().Be("dotnet build App.sln");
    }

    [Theory]
    [InlineData("<UseWPF>true</UseWPF><OutputType>WinExe</OutputType>", "WPF")]
    [InlineData("<UseWindowsForms>true</UseWindowsForms><OutputType>WinExe</OutputType>", "WinForms")]
    [InlineData("<UseMaui>true</UseMaui>", "MAUI")]
    public async Task Desktop_stacks_come_from_project_properties(string properties, string technology)
    {
        using var fixture = new DetectionFixture()
            .With("Desk/Desk.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework>{properties}</PropertyGroup></Project>");

        var profile = await fixture.DetectAsync();

        profile.Technology(technology).Kind.Should().Be(TechnologyKind.Framework);
        profile.Command("dotnet:build").CommandLine.Should().Be("dotnet build Desk/Desk.csproj");
        profile.Command("dotnet:run:Desk").CommandLine.Should().Be("dotnet run --project Desk/Desk.csproj");
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk.Worker", "Worker Service")]
    [InlineData("Microsoft.NET.Sdk.BlazorWebAssembly", "Blazor")]
    [InlineData("MSTest.Sdk/3.6.0", "MSTest")]
    public async Task Sdk_flavors_are_recognized(string sdk, string technology)
    {
        using var fixture = new DetectionFixture().With("Svc/Svc.csproj", $"<Project Sdk=\"{sdk}\" />");

        var profile = await fixture.DetectAsync();

        profile.Technology(technology);
    }

    [Theory]
    [InlineData("NUnit", "NUnit")]
    [InlineData("MSTest.TestFramework", "MSTest")]
    public async Task Test_frameworks_come_from_packages(string package, string framework)
    {
        using var fixture = new DetectionFixture()
            .With("Lib.Tests/Lib.Tests.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"{package}\" Version=\"1.0.0\" /></ItemGroup></Project>");

        var profile = await fixture.DetectAsync();

        profile.Tests.Frameworks.Should().Contain(framework);
        profile.Command("dotnet:test").CommandLine.Should().Be("dotnet test Lib.Tests/Lib.Tests.csproj");
    }

    [Fact]
    public async Task Projects_without_solution_get_per_project_commands()
    {
        using var fixture = new DetectionFixture()
            .With("Api/Api.fsproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />")
            .With("Api.Tests/Api.Tests.fsproj", TestProject);

        var profile = await fixture.DetectAsync();

        profile.Command("dotnet:build:Api").CommandLine.Should().Be("dotnet build Api/Api.fsproj");
        profile.Command("dotnet:test:Api.Tests").CommandLine.Should().Be("dotnet test Api.Tests/Api.Tests.fsproj");
        profile.Technology("F#");
        profile.HasCommand("dotnet:build").Should().BeFalse();
    }

    [Fact]
    public async Task Paths_with_spaces_are_quoted()
    {
        using var fixture = new DetectionFixture().With("My App/My App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");

        var profile = await fixture.DetectAsync();

        profile.Command("dotnet:build").CommandLine.Should().Be("dotnet build \"My App/My App.csproj\"");
    }

    [Fact]
    public async Task Invalid_project_file_is_skipped_with_a_note()
    {
        using var fixture = new DetectionFixture()
            .With("Good/Good.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />")
            .With("Bad/Bad.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>");

        var profile = await fixture.DetectAsync();

        profile.Notes.Should().Contain(n => n.Contains("Bad/Bad.csproj", StringComparison.Ordinal));
        profile.Command("dotnet:build").CommandLine.Should().Be("dotnet build Good/Good.csproj", "the only readable project is the build target");
    }

    [Fact]
    public async Task Too_many_runnable_projects_get_no_run_commands()
    {
        using var fixture = new DetectionFixture().With("All.sln", "");
        for (var i = 0; i < 5; i++)
        {
            fixture.With($"App{i}/App{i}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        }

        var profile = await fixture.DetectAsync();

        profile.Commands.Should().NotContain(c => c.Id.StartsWith("dotnet:run:", StringComparison.Ordinal));
        profile.Command("dotnet:publish").CommandLine.Should().Be("dotnet publish All.sln -c Release");
    }
}
