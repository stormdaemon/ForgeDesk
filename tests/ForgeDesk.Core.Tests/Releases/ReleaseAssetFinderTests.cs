using ForgeDesk.Core.Releases;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Releases;

public sealed class ReleaseAssetFinderTests : IDisposable
{
    private readonly TempDirectory _dir = new("assets");
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("app.zip", true)]
    [InlineData("App.EXE", true)]
    [InlineData("setup.msi", true)]
    [InlineData("app.msix", true)]
    [InlineData("Forge.1.2.0.nupkg", true)]
    [InlineData("tool-linux.tar.gz", true)]
    [InlineData("tool.tgz", true)]
    [InlineData("Forge.dmg", true)]
    [InlineData("Forge-x86_64.AppImage", true)]
    [InlineData("forge_1.2.0_amd64.deb", true)]
    [InlineData("forge-1.2.0.x86_64.rpm", true)]
    [InlineData("forge-1.2.0-py3-none-any.whl", true)]
    [InlineData("forge.jar", true)]
    [InlineData("forge.apk", true)]
    [InlineData("forge.gz", false)]
    [InlineData("forge.dll", false)]
    [InlineData("forge.pdb", false)]
    [InlineData("zip", false)]
    public void Recognizes_artifact_extensions(string name, bool expected) =>
        ReleaseAssetFinder.LooksLikeArtifact(name).Should().Be(expected);

    [Fact]
    public void Finds_recent_artifacts_in_output_folders_newest_first()
    {
        var older = Write("artifacts/forge-1.0.nupkg", TimeSpan.FromDays(3));
        var newest = Write("dist/forge.zip", TimeSpan.FromMinutes(5));
        var rust = Write("target/release/forge-linux.tar.gz", TimeSpan.FromDays(1));
        var dotnet = Write("bin/Release/net10.0/forge.exe", TimeSpan.FromHours(2));
        var nested = Write("src/Forge.App/bin/Release/Forge.msi", TimeSpan.FromHours(4));

        var found = ReleaseAssetFinder.Find(_dir.Path, _now, TestContext.Current.CancellationToken);

        found.Should().Equal(newest, dotnet, nested, rust, older);
    }

    [Fact]
    public void Ignores_old_files_other_folders_and_non_artifacts()
    {
        Write("dist/stale.zip", TimeSpan.FromDays(15));
        Write("dist/readme.md", TimeSpan.FromMinutes(1));
        Write("src/forge.zip", TimeSpan.FromMinutes(1));
        Write("node_modules/pkg/dist/x.zip", TimeSpan.FromMinutes(1));
        Write("dist/node_modules/y.zip", TimeSpan.FromMinutes(1));
        Write("obj/Release/z.exe", TimeSpan.FromMinutes(1));
        Write("a/b/c/bin/Release/too-deep.exe", TimeSpan.FromMinutes(1));

        ReleaseAssetFinder.Find(_dir.Path, _now, TestContext.Current.CancellationToken).Should().BeEmpty();
    }

    [Fact]
    public void Returns_at_most_thirty_files()
    {
        for (var i = 0; i < 40; i++)
        {
            Write($"dist/build-{i:00}.zip", TimeSpan.FromMinutes(i));
        }

        var found = ReleaseAssetFinder.Find(_dir.Path, _now, TestContext.Current.CancellationToken);

        found.Should().HaveCount(ReleaseAssetFinder.MaxResults);
        Path.GetFileName(found[0]).Should().Be("build-00.zip");
    }

    [Fact]
    public void A_missing_folder_yields_nothing() =>
        ReleaseAssetFinder.Find(Path.Combine(_dir.Path, "missing"), _now, TestContext.Current.CancellationToken).Should().BeEmpty();

    private string Write(string relativePath, TimeSpan age)
    {
        var path = _dir.WriteFile(relativePath, "content");
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime - age);
        return path;
    }
}
