using Dapper;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Tests.Infrastructure;

namespace ForgeDesk.Core.Tests.Detection;

public sealed class CustomCommandStoreTests : IAsyncLifetime
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero));
    private TestDatabase _db = null!;
    private CustomCommandStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = new CustomCommandStore(_db.Database, _clock);
        await using var connection = await _db.Database.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO projects (id, name, path, added_at) VALUES ('p1', 'One', '/p1', '2026-01-01T00:00:00Z'), ('p2', 'Two', '/p2', '2026-01-01T00:00:00Z')");
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Added_commands_are_custom_and_listed_in_creation_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _store.AddAsync("p1", "  Start API ", " dotnet watch run ", CommandCategory.Dev, @"src\Api\", ct);
        _clock.Now = _clock.Now.AddMinutes(1);
        await _store.AddAsync("p1", "Seed", "npm run seed", CommandCategory.Run, null, ct);
        await _store.AddAsync("p2", "Other project", "make", CommandCategory.Build, null, ct);

        var commands = await _store.GetAsync("p1", ct);

        first.Id.Should().StartWith("custom:");
        first.Should().Match<DetectedCommand>(c => c.Name == "Start API" && c.CommandLine == "dotnet watch run" && c.WorkingDirectory == "src/Api"
            && c.IsCustom && c.Source == "custom" && c.Category == CommandCategory.Dev);
        commands.Select(c => c.Name).Should().Equal("Start API", "Seed");
        commands[0].Should().BeEquivalentTo(first);
        commands[1].WorkingDirectory.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_changes_every_editable_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var command = await _store.AddAsync("p1", "Test", "npm test", CommandCategory.Test, null, ct);

        await _store.UpdateAsync("p1", command with { Name = "Test (watch)", CommandLine = "npm test -- --watch", Category = CommandCategory.Dev, WorkingDirectory = "./web" }, ct);

        var updated = (await _store.GetAsync("p1", ct)).Single();
        updated.Should().Match<DetectedCommand>(c => c.Id == command.Id && c.Name == "Test (watch)" && c.CommandLine == "npm test -- --watch"
            && c.Category == CommandCategory.Dev && c.WorkingDirectory == "web");
    }

    [Fact]
    public async Task Delete_removes_only_that_command_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var keep = await _store.AddAsync("p1", "Keep", "echo keep", CommandCategory.Other, null, ct);
        var remove = await _store.AddAsync("p1", "Remove", "echo remove", CommandCategory.Other, null, ct);

        await _store.DeleteAsync("p1", remove.Id, ct);
        await _store.DeleteAsync("p1", remove.Id, ct);

        (await _store.GetAsync("p1", ct)).Should().ContainSingle().Which.Id.Should().Be(keep.Id);
    }

    [Fact]
    public async Task Commands_of_another_project_cannot_be_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        var command = await _store.AddAsync("p1", "Build", "make", CommandCategory.Build, null, ct);

        var update = () => _store.UpdateAsync("p2", command with { Name = "Hijack" }, ct);
        await _store.DeleteAsync("p2", command.Id, ct);

        (await update.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
        (await _store.GetAsync("p1", ct)).Single().Name.Should().Be("Build");
    }

    [Fact]
    public async Task Detected_commands_cannot_be_edited_or_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var detected = new DetectedCommand { Id = "npm:build", Name = "build", CommandLine = "npm run build", Source = "package.json" };

        var update = () => _store.UpdateAsync("p1", detected, ct);
        var delete = () => _store.DeleteAsync("p1", "npm:build", ct);

        (await update.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
        (await delete.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Theory]
    [InlineData("", "npm test", null)]
    [InlineData("Name", "   ", null)]
    [InlineData("Name", "echo a\necho b", null)]
    [InlineData("Name", "npm test", "../outside")]
    [InlineData("Name", "npm test", "src/../../outside")]
    [InlineData("Name", "npm test", "/etc")]
    public async Task Invalid_input_is_rejected(string name, string commandLine, string? workingDirectory)
    {
        var act = () => _store.AddAsync("p1", name, commandLine, CommandCategory.Run, workingDirectory, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Adding_to_an_unknown_project_reports_not_found()
    {
        var act = () => _store.AddAsync("nope", "Build", "make", CommandCategory.Build, null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Commands_are_deleted_with_their_project()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.AddAsync("p1", "Build", "make", CommandCategory.Build, null, ct);

        await using (var connection = await _db.Database.OpenConnectionAsync(ct))
        {
            await connection.ExecuteAsync("DELETE FROM projects WHERE id = 'p1'");
        }

        (await _store.GetAsync("p1", ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(".", null)]
    [InlineData("./", null)]
    [InlineData(@".\web\", "web")]
    [InlineData("apps//web/./", "apps/web")]
    public void Working_directories_are_normalized(string? input, string? expected) =>
        CustomCommandStore.NormalizeWorkingDirectory(input).Should().Be(expected);
}
