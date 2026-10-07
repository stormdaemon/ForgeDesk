using Dapper;
using ForgeDesk.Core.Activity;
using ForgeDesk.Core.Analysis;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Git;
using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Core.Runs;
using ForgeDesk.Core.Tests.Detection;
using ForgeDesk.Core.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ForgeDesk.Core.Tests.Projects;

public sealed class ProjectRegistryTests : IAsyncLifetime
{
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 4, 2, 10, 30, 0, TimeSpan.Zero));
    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IActivityLog _activity = Substitute.For<IActivityLog>();
    private readonly IRunService _runs = Substitute.For<IRunService>();
    private readonly TempDirectory _folders = new("projects");
    private TestDatabase _db = null!;
    private ProjectRegistry _registry = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IRunService)).Returns(_runs);
        _git.GetRemotesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ForgeException(ErrorKind.NotARepository, "Not a git repository."));
        _registry = new ProjectRegistry(_db.Database, _git, _activity, services, _clock) { Random = new Random(42) };
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        _folders.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Add_registers_a_folder_with_defaults()
    {
        var path = Folder("my-app");
        var changes = new List<ProjectsChangedEventArgs>();
        _registry.Changed += (_, e) => changes.Add(e);

        var project = await _registry.AddAsync(path + Path.DirectorySeparatorChar, cancellationToken: Ct);

        project.Name.Should().Be("my-app");
        project.Path.Should().Be(path);
        project.Id.Should().HaveLength(32);
        project.AddedAt.Should().Be(_clock.Now);
        project.LastOpenedAt.Should().BeNull();
        project.IsPinned.Should().BeFalse();
        project.GitHub.Should().BeNull();
        AvatarPalette.Colors.Should().Contain(project.Color);
        (await _registry.GetAsync(project.Id, Ct)).Should().BeEquivalentTo(project);
        changes.Should().ContainSingle().Which.ProjectId.Should().Be(project.Id);
        await _activity.Received(1).RecordAsync(
            Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.ProjectAdded && e.ProjectId == project.Id && e.Title == "Added my-app" && e.Detail == path),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Add_uses_the_display_name_and_appends_to_the_sort_order()
    {
        var first = await _registry.AddAsync(Folder("a"), cancellationToken: Ct);
        var second = await _registry.AddAsync(Folder("b"), "  Backend API  ", Ct);

        second.Name.Should().Be("Backend API");
        second.SortOrder.Should().Be(first.SortOrder + 1);
        (await _registry.GetAllAsync(Ct)).Select(p => p.Id).Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task New_projects_prefer_unused_avatar_colors()
    {
        var colors = new List<string?>();
        for (var i = 0; i < AvatarPalette.Colors.Count; i++)
        {
            colors.Add((await _registry.AddAsync(Folder($"p{i}"), cancellationToken: Ct)).Color);
        }

        colors.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(AvatarPalette.Colors);
    }

    [Fact]
    public async Task Add_links_the_origin_GitHub_repository()
    {
        var path = Folder("linked");
        _git.GetRemotesAsync(path, Arg.Any<CancellationToken>()).Returns(
        [
            new GitRemote("upstream", "https://github.com/upstream-org/tool.git", null),
            new GitRemote("origin", "git@github.com:me/tool.git", null),
        ]);

        var project = await _registry.AddAsync(path, cancellationToken: Ct);

        project.GitHub.Should().Be(new GitHubRepoRef("me", "tool"));
        (await _registry.GetAsync(project.Id, Ct))!.GitHub.Should().Be(new GitHubRepoRef("me", "tool"));
    }

    [Fact]
    public async Task Add_falls_back_to_the_first_GitHub_remote()
    {
        var path = Folder("fork");
        _git.GetRemotesAsync(path, Arg.Any<CancellationToken>()).Returns(
        [
            new GitRemote("origin", "https://gitlab.com/me/tool.git", null),
            new GitRemote("mirror", "https://example.com/tool.git", "https://github.com/me/tool-mirror"),
        ]);

        var project = await _registry.AddAsync(path, cancellationToken: Ct);

        project.GitHub.Should().Be(new GitHubRepoRef("me", "tool-mirror"));
    }

    [Theory]
    [InlineData(ErrorKind.GitNotFound)]
    [InlineData(ErrorKind.GitCommandFailed)]
    public async Task Git_failures_do_not_prevent_registration(ErrorKind kind)
    {
        var path = Folder("no-git");
        _git.GetRemotesAsync(path, Arg.Any<CancellationToken>()).ThrowsAsync(new ForgeException(kind, "git failed"));

        var project = await _registry.AddAsync(path, cancellationToken: Ct);

        project.GitHub.Should().BeNull();
    }

    [Fact]
    public async Task Adding_the_same_folder_twice_is_rejected()
    {
        var path = Folder("dup");
        await _registry.AddAsync(path, cancellationToken: Ct);

        var act = () => _registry.AddAsync(Path.Combine(path, "..", "dup"), cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
        (await _registry.GetAllAsync(Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Paths_differing_only_by_case_are_the_same_project()
    {
        var path = Folder("CaseApp");
        await _registry.AddAsync(path, cancellationToken: Ct);
        var otherCase = Path.Combine(Path.GetDirectoryName(path)!, "caseapp");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(otherCase);
        }

        var act = () => _registry.AddAsync(otherCase, cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.AlreadyExists);
        if (OperatingSystem.IsWindows())
        {
            (await _registry.FindByPathAsync(otherCase.ToUpperInvariant(), Ct)).Should().NotBeNull();
        }
        else if (!OperatingSystem.IsMacOS())
        {
            error.Message.Should().Contain("letter case");
            (await _registry.FindByPathAsync(otherCase, Ct)).Should().BeNull("on a case-sensitive file system it is another folder");
        }
    }

    [Fact]
    public async Task Non_ascii_paths_differing_only_by_case_are_the_same_project()
    {
        // SQLite's NOCASE only folds A-Z; NTFS (and OrdinalIgnoreCase) also fold É/é.
        var path = Folder("Été-Öl");
        await _registry.AddAsync(path, cancellationToken: Ct);
        var otherCase = Path.Combine(Path.GetDirectoryName(path)!, "été-öl");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(otherCase);
        }

        var act = () => _registry.AddAsync(otherCase, cancellationToken: Ct);

        var error = (await act.Should().ThrowAsync<ForgeException>()).Which;
        error.Kind.Should().Be(ErrorKind.AlreadyExists);
        (await _registry.GetAllAsync(Ct)).Should().ContainSingle();
        if (OperatingSystem.IsWindows())
        {
            (await _registry.FindByPathAsync(otherCase, Ct)).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Missing_folder_is_reported_as_path_not_found()
    {
        var act = () => _registry.AddAsync(_folders.Combine("does-not-exist"), cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);
    }

    [Fact]
    public async Task A_file_is_not_a_project()
    {
        var file = _folders.WriteFile("notes.txt", "hello");

        var act = () => _registry.AddAsync(file, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Drive_roots_and_the_user_profile_are_too_broad()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var addRoot = () => _registry.AddAsync(driveRoot, cancellationToken: Ct);
        var addProfile = () => _registry.AddAsync(profile, cancellationToken: Ct);

        (await addRoot.Should().ThrowAsync<ForgeException>()).Which.Should().Match<ForgeException>(e => e.Kind == ErrorKind.InvalidInput && e.Message.Contains("root", StringComparison.Ordinal));
        (await addProfile.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_paths_are_invalid_input(string path)
    {
        var act = () => _registry.AddAsync(path, cancellationToken: Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task FindByPath_matches_normalized_paths_and_tolerates_invalid_ones()
    {
        var path = Folder("findme");
        var project = await _registry.AddAsync(path, cancellationToken: Ct);

        (await _registry.FindByPathAsync(path + Path.DirectorySeparatorChar, Ct))!.Id.Should().Be(project.Id);
        (await _registry.FindByPathAsync(Path.Combine(path, "sub", ".."), Ct))!.Id.Should().Be(project.Id);
        (await _registry.FindByPathAsync(Folder("other"), Ct)).Should().BeNull();
        (await _registry.FindByPathAsync("   ", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Update_changes_name_group_color_pin_and_order()
    {
        var project = await _registry.AddAsync(Folder("upd"), cancellationToken: Ct);
        var changed = 0;
        _registry.Changed += (_, _) => changed++;

        var updated = await _registry.UpdateAsync(project with { Name = " Renamed ", Group = " Work ", Color = "#abcdef", IsPinned = true, SortOrder = 7, Path = "ignored" }, Ct);

        updated.Should().BeEquivalentTo(project with { Name = "Renamed", Group = "Work", Color = "#ABCDEF", IsPinned = true, SortOrder = 7 });
        (await _registry.GetAsync(project.Id, Ct)).Should().BeEquivalentTo(updated);
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Update_clears_blank_group_and_color()
    {
        var project = await _registry.AddAsync(Folder("clear"), cancellationToken: Ct);

        var updated = await _registry.UpdateAsync(project with { Group = "  ", Color = null }, Ct);

        updated.Group.Should().BeNull();
        updated.Color.Should().BeNull();
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Name", "red")]
    [InlineData("Name", "#12345")]
    public async Task Update_validates_input(string name, string? color)
    {
        var project = await _registry.AddAsync(Folder("val"), cancellationToken: Ct);

        var act = () => _registry.UpdateAsync(project with { Name = name, Color = color }, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public async Task Update_of_a_removed_project_is_not_found()
    {
        var project = await _registry.AddAsync(Folder("gone"), cancellationToken: Ct);
        await _registry.RemoveAsync(project.Id, Ct);

        var act = () => _registry.UpdateAsync(project, Ct);

        (await act.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Relocate_points_the_project_to_its_new_folder()
    {
        var project = await _registry.AddAsync(Folder("old-place"), cancellationToken: Ct);
        await _registry.SaveSnapshotAsync(new ProjectSnapshot { ProjectId = project.Id, FolderExists = false }, Ct);
        var newPath = Folder("new-place");
        _git.GetRemotesAsync(newPath, Arg.Any<CancellationToken>()).Returns([new GitRemote("origin", "https://github.com/acme/moved.git", null)]);

        var relocated = await _registry.RelocateAsync(project.Id, newPath, Ct);

        relocated.Path.Should().Be(newPath);
        relocated.GitHub.Should().Be(new GitHubRepoRef("acme", "moved"));
        (await _registry.GetAsync(project.Id, Ct)).Should().BeEquivalentTo(relocated);
        (await _registry.GetCachedSnapshotAsync(project.Id, Ct)).Should().BeNull("the snapshot described the old location");
        await _activity.Received(1).RecordAsync(Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.ProjectRelocated && e.ProjectId == project.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relocate_rejects_folders_used_by_another_project_and_missing_folders()
    {
        var first = await _registry.AddAsync(Folder("first"), cancellationToken: Ct);
        var second = await _registry.AddAsync(Folder("second"), cancellationToken: Ct);

        var toTaken = () => _registry.RelocateAsync(first.Id, second.Path, Ct);
        var toMissing = () => _registry.RelocateAsync(first.Id, _folders.Combine("missing"), Ct);
        var unknown = () => _registry.RelocateAsync("unknown", Folder("x"), Ct);

        (await toTaken.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.AlreadyExists);
        (await toMissing.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.PathNotFound);
        (await unknown.Should().ThrowAsync<ForgeException>()).Which.Kind.Should().Be(ErrorKind.NotFound);
        (await _registry.GetAsync(first.Id, Ct))!.Path.Should().Be(first.Path);
    }

    [Fact]
    public async Task Relocating_to_the_same_folder_is_allowed()
    {
        var project = await _registry.AddAsync(Folder("same"), cancellationToken: Ct);

        var relocated = await _registry.RelocateAsync(project.Id, project.Path, Ct);

        relocated.Path.Should().Be(project.Path);
    }

    [Fact]
    public async Task Remove_cascades_to_project_data_and_keeps_a_journal_entry()
    {
        var project = await _registry.AddAsync(Folder("doomed"), cancellationToken: Ct);
        var survivor = await _registry.AddAsync(Folder("survivor"), cancellationToken: Ct);
        await SeedProjectDataAsync(project.Id);
        await SeedProjectDataAsync(survivor.Id);
        var changes = new List<string?>();
        _registry.Changed += (_, e) => changes.Add(e.ProjectId);

        await _registry.RemoveAsync(project.Id, Ct);

        (await _registry.GetAsync(project.Id, Ct)).Should().BeNull();
        await using var connection = await _db.Database.OpenConnectionAsync(Ct);
        foreach (var table in new[] { "work_items", "runs", "activity", "custom_commands" })
        {
            (await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE project_id = @Id", new { project.Id })).Should().Be(0, table);
            (await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE project_id = @Id", new { survivor.Id })).Should().Be(1, table);
        }

        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM work_item_events")).Should().Be(1);
        await _runs.Received(1).DeleteHistoryAsync(project.Id, Arg.Any<CancellationToken>());
        await _activity.Received(1).RecordAsync(
            Arg.Is<ActivityEntry>(e => e.Kind == ActivityKind.ProjectRemoved && e.ProjectId == null && e.Title == "Removed doomed"),
            Arg.Any<CancellationToken>());
        changes.Should().Equal(project.Id);
    }

    [Fact]
    public async Task Remove_tolerates_run_history_failures_and_unknown_ids()
    {
        var project = await _registry.AddAsync(Folder("runs-fail"), cancellationToken: Ct);
        _runs.DeleteHistoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("log file locked"));

        await _registry.RemoveAsync(project.Id, Ct);
        await _registry.RemoveAsync("unknown-id", Ct);

        (await _registry.GetAllAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Activity_failures_never_break_registration()
    {
        _activity.RecordAsync(Arg.Any<ActivityEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("journal down"));

        var project = await _registry.AddAsync(Folder("journal"), cancellationToken: Ct);

        (await _registry.GetAsync(project.Id, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task MarkOpened_records_the_time()
    {
        var project = await _registry.AddAsync(Folder("open"), cancellationToken: Ct);
        _clock.Now = _clock.Now.AddHours(3);

        await _registry.MarkOpenedAsync(project.Id, Ct);
        await _registry.MarkOpenedAsync("unknown", Ct);

        (await _registry.GetAsync(project.Id, Ct))!.LastOpenedAt.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task Snapshot_profile_and_health_caches_round_trip()
    {
        var project = await _registry.AddAsync(Folder("cache"), cancellationToken: Ct);
        var snapshot = new ProjectSnapshot
        {
            ProjectId = project.Id,
            CapturedAt = _clock.Now,
            IsGitRepository = true,
            Branch = "main",
            Ahead = 2,
            Technologies = ["React", "Vite"],
            Ci = new CiSummary { State = CiState.Failure, Branch = "main" },
            LastRun = new RunOutcomeSummary("Build", false, _clock.Now),
            Attention = [new AttentionReason(AttentionLevel.Critical, "CI is failing on main.", "GitHub")],
        };
        var profile = new ProjectProfile
        {
            DetectedAt = _clock.Now,
            PrimaryLanguage = "TypeScript",
            Languages = [new LanguageShare("TypeScript", 3, 1200, 100, "#3178C6")],
            Technologies = [new Technology("React", TechnologyKind.Framework, "package.json")],
            Commands = [new DetectedCommand { Id = "npm:dev", Name = "dev", CommandLine = "npm run dev", Category = CommandCategory.Dev, Source = "package.json" }],
            Tests = new TestInfo(true, ["Vitest"], ["src/__tests__"]),
            Workflows = [new WorkflowFile("CI", ".github/workflows/ci.yml", ["push"])],
            Structure = [new StructureEntry("src", true, StructureKind.Source, "Source code")],
        };
        var health = new ProjectHealthReport { GeneratedAt = _clock.Now, Duration = TimeSpan.FromSeconds(3), Score = 87, Tests = profile.Tests };

        await _registry.SaveSnapshotAsync(snapshot, Ct);
        await _registry.SaveProfileAsync(project.Id, profile, Ct);
        await _registry.SaveHealthAsync(project.Id, health, Ct);

        (await _registry.GetCachedSnapshotAsync(project.Id, Ct)).Should().BeEquivalentTo(snapshot);
        (await _registry.GetCachedProfileAsync(project.Id, Ct)).Should().BeEquivalentTo(profile);
        (await _registry.GetCachedHealthAsync(project.Id, Ct)).Should().BeEquivalentTo(health);
        await using var connection = await _db.Database.OpenConnectionAsync(Ct);
        (await connection.ExecuteScalarAsync<string>("SELECT snapshot_json FROM projects WHERE id = @Id", new { project.Id }))
            .Should().Contain("\"Failure\"", "enums are stored by name so reordering them never corrupts caches");
    }

    [Theory]
    [InlineData("snapshot_json", "{ not json")]
    [InlineData("profile_json", "[1, 2, 3]")]
    [InlineData("health_json", "{\"score\": \"high\"}")]
    [InlineData("snapshot_json", "{\"capturedAt\": \"2026-01-01T00:00:00Z\"}")]
    public async Task Corrupted_caches_read_as_null(string column, string json)
    {
        var project = await _registry.AddAsync(Folder("corrupt"), cancellationToken: Ct);
        await using (var connection = await _db.Database.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync($"UPDATE projects SET {column} = @Json WHERE id = @Id", new { Json = json, project.Id });
        }

        (await _registry.GetCachedSnapshotAsync(project.Id, Ct)).Should().BeNull();
        (await _registry.GetCachedProfileAsync(project.Id, Ct)).Should().BeNull();
        (await _registry.GetCachedHealthAsync(project.Id, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Caches_of_unknown_projects_are_empty_and_saving_them_is_harmless()
    {
        await _registry.SaveSnapshotAsync(new ProjectSnapshot { ProjectId = "ghost" }, Ct);

        (await _registry.GetCachedSnapshotAsync("ghost", Ct)).Should().BeNull();
    }

    private string Folder(string name)
    {
        var path = _folders.Combine(name);
        Directory.CreateDirectory(path);
        return PathUtil.Normalize(path);
    }

    private async Task SeedProjectDataAsync(string projectId)
    {
        await using var connection = await _db.Database.OpenConnectionAsync(Ct);
        var itemId = Ids.New();
        await connection.ExecuteAsync(
            """
            INSERT INTO work_items (id, project_id, number, title, status, priority, created_at, updated_at)
            VALUES (@ItemId, @ProjectId, 1, 'Task', 1, 0, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            INSERT INTO work_item_events (work_item_id, at, kind, summary) VALUES (@ItemId, '2026-01-01T00:00:00Z', 0, 'Created');
            INSERT INTO runs (id, project_id, label, command_line, working_dir, category, status, started_at, log_path)
            VALUES (@RunId, @ProjectId, 'Build', 'make', '.', 2, 2, '2026-01-01T00:00:00Z', 'run.log');
            INSERT INTO activity (project_id, at, kind, outcome, title) VALUES (@ProjectId, '2026-01-01T00:00:00Z', 10, 1, 'Commit');
            INSERT INTO custom_commands (id, project_id, name, command_line, category, created_at)
            VALUES (@CommandId, @ProjectId, 'Seed', 'npm run seed', 9, '2026-01-01T00:00:00Z');
            """,
            new { ItemId = itemId, ProjectId = projectId, RunId = Ids.New(), CommandId = Ids.New() });
    }
}
