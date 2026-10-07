using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;
using ForgeDesk.Presentation.Files;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Files.Support;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using NSubstitute;

namespace ForgeDesk.Presentation.Tests.Files;

public sealed class FileViewerViewModelTests : IDisposable
{
    private readonly FilesHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private FileViewerViewModel Viewer() => new(_harness.Context, _harness.Services, _harness.Files);

    [Fact]
    public async Task Nothing_is_shown_until_a_file_is_selected()
    {
        var viewer = Viewer();

        viewer.IsNone.Should().BeTrue();
        viewer.HasPath.Should().BeFalse();
        viewer.OpenInEditorCommand.CanExecute(null).Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Text_files_show_their_content_and_facts()
    {
        _harness.Files.SetText("src/main.cs", "a\nb\nc", size: 2048);
        var viewer = Viewer();

        await viewer.LoadAsync("src/main.cs", 2);

        viewer.Kind.Should().Be(FileViewKind.Text);
        viewer.ShowContent.Should().BeTrue();
        viewer.Text.Should().Be("a\nb\nc");
        viewer.ScrollToLine.Should().Be(2);
        viewer.Name.Should().Be("main.cs");
        viewer.SizeText.Should().Be(Format.Bytes(2048));
        viewer.EncodingText.Should().Be("UTF-8");
        viewer.LineEndingsText.Should().Be("LF");
        viewer.LineCountText.Should().Be("3 lines");
        viewer.ModifiedText.Should().StartWith("Modified ");
        viewer.IsTruncated.Should().BeFalse();
        viewer.Breadcrumbs.Select(b => b.Name).Should().Equal("forge-app", "src", "main.cs");
        viewer.Breadcrumbs.Select(b => b.RelativePath).Should().Equal(string.Empty, "src", "src/main.cs");
        viewer.Breadcrumbs.Last().IsLast.Should().BeTrue();
    }

    [Fact]
    public async Task Loading_the_same_file_again_only_moves_to_the_line()
    {
        _harness.Files.SetText("a.txt", "x\ny\nz");
        var viewer = Viewer();
        await viewer.LoadAsync("a.txt", 1);
        var lines = new List<int?>();
        viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileViewerViewModel.ScrollToLine))
            {
                lines.Add(viewer.ScrollToLine);
            }
        };

        await viewer.LoadAsync("a.txt", 3);
        await viewer.LoadAsync("a.txt", 3);

        _harness.Files.ReadCalls.Should().HaveCount(1);
        viewer.ScrollToLine.Should().Be(3);
        lines.Should().Equal(null, 3, null, 3);
    }

    [Fact]
    public async Task Large_files_are_truncated_with_a_notice()
    {
        _harness.Files.SetText("big.log", "start", truncated: true, size: 12 * 1024 * 1024);
        var viewer = Viewer();

        await viewer.LoadAsync("big.log");

        viewer.IsTruncated.Should().BeTrue();
        viewer.TruncatedMessage.Should().Contain(Format.Bytes(FileViewerViewModel.MaxTextBytes)).And.Contain(Format.Bytes(12 * 1024 * 1024));
    }

    [Fact]
    public async Task Images_are_previewed_from_their_full_path()
    {
        _harness.Files.SetContent("assets/logo.png", FileContentKind.Image);
        var viewer = Viewer();

        await viewer.LoadAsync("assets/logo.png");

        viewer.IsImage.Should().BeTrue();
        viewer.ImagePath.Should().Be(Path.Combine(_harness.Root, "assets", "logo.png"));
        viewer.Text.Should().BeNull();
        viewer.IsImageFit.Should().BeTrue();

        viewer.ToggleZoomCommand.Execute(null);
        viewer.ZoomText.Should().Be("100%");
    }

    [Fact]
    public async Task Binary_files_offer_the_default_app()
    {
        _harness.Files.SetContent("tool.exe", FileContentKind.Binary);
        var viewer = Viewer();

        await viewer.LoadAsync("tool.exe");
        viewer.IsBinary.Should().BeTrue();
        viewer.OpenWithDefaultAppCommand.Execute(null);

        _harness.Shell.Received().OpenWithDefaultApp(Path.Combine(_harness.Root, "tool.exe"));
    }

    [Fact]
    public async Task Read_errors_are_shown_and_can_be_retried()
    {
        _harness.Files.FailRead("locked.db", new ForgeException(ErrorKind.PermissionDenied, "'locked.db' is locked by another program."));
        var viewer = Viewer();

        await viewer.LoadAsync("locked.db");

        viewer.Error.Should().NotBeNull();
        viewer.Error!.Kind.Should().Be(ErrorKind.PermissionDenied);
        viewer.ShowContent.Should().BeFalse();
        viewer.IsNone.Should().BeFalse();

        _harness.Files.SetText("locked.db", "now readable");
        await viewer.RetryCommand.ExecuteAsync(null);

        viewer.Error.Should().BeNull();
        viewer.Text.Should().Be("now readable");
    }

    [Fact]
    public async Task A_newer_load_wins_over_a_stale_one_which_is_cancelled()
    {
        var slow = new TaskCompletionSource<FileContent>();
        var token = CancellationToken.None;
        _harness.Files.SetRead("slow.txt", ct =>
        {
            token = ct;
            return slow.Task;
        });
        _harness.Files.SetText("fast.txt", "fast");
        var viewer = Viewer();

        var first = viewer.LoadAsync("slow.txt");
        viewer.IsLoading.Should().BeTrue();
        await viewer.LoadAsync("fast.txt");

        token.IsCancellationRequested.Should().BeTrue();
        slow.SetResult(new FileContent { RelativePath = "slow.txt", Kind = FileContentKind.Text, Text = "stale" });
        await first;

        viewer.RelativePath.Should().Be("fast.txt");
        viewer.Text.Should().Be("fast");
        viewer.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_a_load_stops_the_spinner_and_explains()
    {
        var slow = new TaskCompletionSource<FileContent>();
        _harness.Files.SetRead("slow.txt", ct =>
        {
            ct.Register(() => slow.TrySetCanceled(ct));
            return slow.Task;
        });
        var viewer = Viewer();

        var load = viewer.LoadAsync("slow.txt");
        viewer.CancelLoadCommand.CanExecute(null).Should().BeTrue();
        viewer.CancelLoadCommand.Execute(null);
        await load;

        viewer.IsLoading.Should().BeFalse();
        viewer.Error!.Kind.Should().Be(ErrorKind.Cancelled);
    }

    [Fact]
    public async Task Folders_show_a_summary_without_reading()
    {
        var viewer = Viewer();

        viewer.ShowFolder("src", "src", 2, 5);

        viewer.IsFolder.Should().BeTrue();
        viewer.HasFile.Should().BeFalse();
        viewer.FolderSummary.Should().Be("2 folders · 5 files");
        _harness.Files.ReadCalls.Should().BeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Git_state_follows_the_status()
    {
        _harness.Files.SetText("a.txt", "x");
        var viewer = Viewer();
        await viewer.LoadAsync("a.txt");

        viewer.ApplyDecorations(FileDecorations.From(TestData.Status(entries: TestData.Modified("a.txt"))));

        viewer.GitState.Should().Be(FileGitState.Modified);
        viewer.GitStateText.Should().Be("Modified");
        viewer.HasGitState.Should().BeTrue();
    }

    [Fact]
    public async Task Open_in_editor_passes_the_revealed_line()
    {
        _harness.Files.SetText("a.txt", "x\ny");
        var viewer = Viewer();
        await viewer.LoadAsync("a.txt", 2);

        viewer.OpenInEditorCommand.Execute(null);
        viewer.CopyRelativePathCommand.Execute(null);

        _harness.Shell.Received().OpenInEditor(Path.Combine(_harness.Root, "a.txt"), 2);
        _harness.Shell.Received().CopyToClipboard("a.txt");
    }
}
