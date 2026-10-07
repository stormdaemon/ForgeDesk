using ForgeDesk.Core.GitHub;
using ForgeDesk.Core.Projects;
using ForgeDesk.Presentation.Dashboard;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using ForgeDesk.Presentation.Workspace;
using static ForgeDesk.Presentation.Tests.Dashboard.DashboardTestData;

namespace ForgeDesk.Presentation.Tests.Dashboard;

public sealed class ProjectCardViewModelTests
{
    private readonly RecordingHost _host = new();

    [Fact]
    public void Before_any_status_the_card_says_it_is_checking()
    {
        var card = Card(Project("forge"));

        card.IsChecking.Should().BeTrue();
        card.Status.Should().Be(StatusTone.None);
        card.StatusText.Should().Be("Checking…");
        card.BranchText.Should().BeNull();
        card.AttentionItems.Should().BeEmpty();
        card.AttentionRank.Should().Be(1);
    }

    [Fact]
    public void A_healthy_project_is_all_clear()
    {
        var project = Project("forge");
        var card = Card(project);

        card.Apply(Snapshot(project, ci: CiState.Success, language: "C#", technologies: ["C#", ".NET", "WPF", "SQLite", "Docker"]));

        card.IsHealthy.Should().BeTrue();
        card.Status.Should().Be(StatusTone.Success);
        card.StatusText.Should().Be("All clear");
        card.BranchText.Should().Be("main");
        card.ChangedFilesText.Should().Be("Clean");
        card.HasCi.Should().BeTrue();
        card.PrimaryLanguage.Should().Be("C#");
        card.Technologies.Should().Equal(".NET", "WPF", "SQLite");
    }

    [Fact]
    public void Attention_reasons_are_listed_most_severe_first_with_their_tab()
    {
        var project = Project("forge");
        var card = Card(project);

        card.Apply(Snapshot(project, changed: 3, ci: CiState.Failure, attention:
        [
            Critical("CI is failing on main.", "GitHub"),
            Warning("2 commits behind origin/main."),
            Info("3 uncommitted changes."),
            Info("1 commit not pushed."),
        ]));

        card.NeedsAttention.Should().BeTrue();
        card.Status.Should().Be(StatusTone.Danger);
        card.AttentionItems.Should().HaveCount(ProjectCardViewModel.MaxAttentionItems);
        card.AttentionItems[0].Section.Should().Be(WorkspaceSection.GitHub);
        card.AttentionItems[0].Tone.Should().Be(StatusTone.Danger);
        card.TopAttention!.Message.Should().Be("CI is failing on main.");
        card.MoreAttentionText.Should().Be("+1 more");
        card.ChangedFilesText.Should().Be("3 changes");
    }

    [Fact]
    public void Info_level_notes_do_not_count_as_needing_attention()
    {
        var project = Project("forge");
        var card = Card(project);

        card.Apply(Snapshot(project, changed: 2, attention: [Info("2 uncommitted changes.")]));

        card.NeedsAttention.Should().BeFalse();
        card.Status.Should().Be(StatusTone.Info);
        card.StatusText.Should().Be("Work in progress");
    }

    [Fact]
    public void A_missing_folder_puts_the_card_in_the_warning_state()
    {
        var project = Project("gone");
        var card = Card(project);

        card.Apply(Snapshot(project, folderExists: false, attention: [Critical("The project folder was moved or deleted.", "Overview")]));

        card.IsFolderMissing.Should().BeTrue();
        card.NeedsAttention.Should().BeTrue();
        card.Status.Should().Be(StatusTone.Warning);
        card.StatusText.Should().Be("Folder not found");
        card.AttentionRank.Should().Be(5);
        card.AttentionItems.Should().BeEmpty("the card shows Locate and Remove instead");
        card.BranchText.Should().BeNull();
    }

    [Fact]
    public void Running_commands_show_first_in_the_status()
    {
        var project = Project("forge");
        var card = Card(project);
        card.Apply(Snapshot(project));

        card.IsRunning = true;

        card.Status.Should().Be(StatusTone.Running);
        card.StatusText.Should().Be("Running");
    }

    [Fact]
    public void Branch_text_explains_detached_heads_and_plain_folders()
    {
        var project = Project("forge");
        var detached = Card(project);
        detached.Apply(Snapshot(project, branch: null) with { IsDetachedHead = true });
        var plain = Card(project);
        plain.Apply(Snapshot(project, branch: null, isGit: false));

        detached.BranchText.Should().Be("Detached HEAD");
        plain.BranchText.Should().Be("Not a Git repository");
    }

    [Fact]
    public void Ahead_and_behind_are_explained_in_the_tooltip()
    {
        var project = Project("forge");
        var card = Card(project);

        card.Apply(Snapshot(project, ahead: 2, behind: 1));

        card.HasAhead.Should().BeTrue();
        card.HasBehind.Should().BeTrue();
        card.SyncToolTip.Should().Be("Tracking origin/main · 2 commits to push, 1 commit to pull");
    }

    [Fact]
    public void An_older_snapshot_never_replaces_a_newer_one()
    {
        var project = Project("forge");
        var card = Card(project);
        card.Apply(Snapshot(project, capturedAt: TestData.Now, changed: 5)).Should().BeTrue();

        var applied = card.Apply(Snapshot(project, capturedAt: TestData.Now.AddMinutes(-5), changed: 1));

        applied.Should().BeFalse();
        card.ChangedFiles.Should().Be(5);
    }

    [Fact]
    public void A_status_problem_is_listed_with_the_reasons()
    {
        var project = Project("forge");
        var card = Card(project);

        card.Apply(Snapshot(project, problem: "Git is not installed."));

        card.Problem.Should().Be("Git is not installed.");
        card.AttentionItems.Should().ContainSingle(i => i.Message == "Git is not installed." && i.Section == null);
        card.IsHealthy.Should().BeFalse();
    }

    [Theory]
    [InlineData("forge", true)]
    [InlineData("FORGE", true)]
    [InlineData("/dev", true)]
    [InlineData("feature/login", true)]
    [InlineData("typescript", true)]
    [InlineData("react", true)]
    [InlineData("work", true)]
    [InlineData("forge work", true)]
    [InlineData("python", false)]
    [InlineData("forge python", false)]
    public void Search_matches_name_path_branch_language_technologies_and_group(string text, bool expected)
    {
        var project = Project("forge-app", group: "Work");
        var card = Card(project);
        card.Apply(Snapshot(project, branch: "feature/login", language: "TypeScript", technologies: ["React"]));

        card.Matches(DashboardOrdering.SearchTerms(text)).Should().Be(expected);
    }

    [Fact]
    public void Commands_are_delegated_to_the_page()
    {
        var project = Project("forge");
        var card = Card(project);
        var item = new AttentionItemViewModel(AttentionLevel.Warning, "Behind", WorkspaceSection.Git);

        card.OpenCommand.Execute(null);
        card.OpenAttentionCommand.Execute(item);
        card.RemoveCommand.Execute(null);
        card.LocateCommand.Execute(null);

        _host.Calls.Should().Equal("open:", "open:Git", "remove", "locate");
    }

    [Fact]
    public void The_editor_entry_names_the_detected_editor()
    {
        _host.EditorName = "Visual Studio Code";
        var card = Card(Project("forge"));

        card.OpenInEditorText.Should().Be("Open in Visual Studio Code");
        card.CanOpenInEditor.Should().BeTrue();
    }

    [Theory]
    [InlineData("Git", WorkspaceSection.Git)]
    [InlineData("github", WorkspaceSection.GitHub)]
    [InlineData("Commands", WorkspaceSection.Commands)]
    [InlineData(null, null)]
    [InlineData("Nowhere", null)]
    public void Attention_sections_map_to_workspace_tabs(string? section, WorkspaceSection? expected)
    {
        var item = AttentionItemViewModel.From(new AttentionReason(AttentionLevel.Warning, "x", section));

        item.Section.Should().Be(expected);
        item.CanOpen.Should().Be(expected is not null);
    }

    private ProjectCardViewModel Card(Project project) => new(project, _host);

    internal sealed class RecordingHost : IProjectCardHost
    {
        public List<string> Calls { get; } = [];

        public string? EditorName { get; set; }

        public Task OpenAsync(ProjectCardViewModel card, WorkspaceSection? section)
        {
            Calls.Add($"open:{section}");
            return Task.CompletedTask;
        }

        public void OpenInExplorer(ProjectCardViewModel card) => Calls.Add("explorer");

        public void OpenInEditor(ProjectCardViewModel card) => Calls.Add("editor");

        public void OpenTerminal(ProjectCardViewModel card) => Calls.Add("terminal");

        public void CopyPath(ProjectCardViewModel card) => Calls.Add("copy");

        public Task TogglePinAsync(ProjectCardViewModel card) => Record("pin");

        public Task RenameAsync(ProjectCardViewModel card) => Record("rename");

        public Task SetGroupAsync(ProjectCardViewModel card) => Record("group");

        public Task RemoveAsync(ProjectCardViewModel card) => Record("remove");

        public Task LocateAsync(ProjectCardViewModel card) => Record("locate");

        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }
}
