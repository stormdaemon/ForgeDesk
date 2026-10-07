using ForgeDesk.Core.Detection;
using ForgeDesk.Core.Runs;
using ForgeDesk.Presentation.Commands;
using ForgeDesk.Presentation.Infrastructure;
using ForgeDesk.Presentation.Tests.Workspace.Support;
using NSubstitute;
using static ForgeDesk.Presentation.Tests.Commands.Support.CommandsHarness;

namespace ForgeDesk.Presentation.Tests.Commands;

public sealed class RunLogAndEditorTests
{
    private static RunLogViewModel LogWith(params string[] lines)
    {
        var log = new RunLogViewModel(ImmediateDispatcher.Instance, Timeout.InfiniteTimeSpan);
        var session = Substitute.For<IRunSession>();
        session.GetLines(Arg.Any<long>()).Returns(lines.Select((t, i) => Line(i, t)).ToList());
        log.Attach(session);
        return log;
    }

    [Fact]
    public void Search_marks_matches_and_cycles_through_them()
    {
        var log = LogWith("Compiling a", "warning: unused", "Compiling b", "done");
        LogLineViewModel? scrolledTo = null;
        log.ScrollToLineRequested += (_, line) => scrolledTo = line;

        log.SearchText = "compiling";

        log.MatchCount.Should().Be(2);
        log.MatchSummary.Should().Be("1 of 2");
        log.Lines[0].IsCurrentMatch.Should().BeTrue();
        scrolledTo.Should().BeSameAs(log.Lines[0]);
        log.IsFollowing.Should().BeFalse("looking at a result pauses the auto-scroll");

        log.NextMatchCommand.Execute(null);
        log.MatchSummary.Should().Be("2 of 2");
        log.Lines[2].IsCurrentMatch.Should().BeTrue();
        log.Lines[0].IsCurrentMatch.Should().BeFalse();

        log.NextMatchCommand.Execute(null);
        log.CurrentMatchIndex.Should().Be(0, "next wraps around");

        log.PreviousMatchCommand.Execute(null);
        log.CurrentMatchIndex.Should().Be(1);

        log.ClearSearchCommand.Execute(null);
        log.MatchCount.Should().Be(0);
        log.Lines.Should().OnlyContain(l => !l.IsMatch && !l.IsCurrentMatch);
        log.MatchSummary.Should().BeEmpty();
    }

    [Fact]
    public void Search_without_results_says_so()
    {
        var log = LogWith("a", "b");

        log.SearchText = "zzz";

        log.MatchSummary.Should().Be("No results");
        log.NextMatchCommand.Execute(null);
        log.CurrentMatchIndex.Should().Be(-1);
    }

    [Fact]
    public void New_lines_matching_the_search_are_counted()
    {
        var session = Substitute.For<IRunSession>();
        session.GetLines(Arg.Any<long>()).Returns([Line(0, "test a passed")]);
        var log = new RunLogViewModel(ImmediateDispatcher.Instance, Timeout.InfiniteTimeSpan);
        log.Attach(session);
        log.SearchText = "failed";

        session.LineReceived += Raise.Event<EventHandler<RunLogLine>>(session, Line(1, "test b failed"));
        log.FlushPendingLines();

        log.MatchCount.Should().Be(1);
        log.Lines[1].IsMatch.Should().BeTrue();
    }

    [Fact]
    public void Jump_to_latest_resumes_following()
    {
        var log = LogWith("a");
        var requested = false;
        log.ScrollToEndRequested += (_, _) => requested = true;
        log.IsFollowing = false;

        log.JumpToLatestCommand.Execute(null);

        log.IsFollowing.Should().BeTrue();
        requested.Should().BeTrue();
    }

    [Fact]
    public void Lines_appended_after_detach_are_ignored()
    {
        var session = Substitute.For<IRunSession>();
        session.GetLines(Arg.Any<long>()).Returns([]);
        var log = new RunLogViewModel(ImmediateDispatcher.Instance, Timeout.InfiniteTimeSpan);
        log.Attach(session);
        log.Detach();

        session.LineReceived += Raise.Event<EventHandler<RunLogLine>>(session, Line(0, "late"));
        log.FlushPendingLines();

        log.Lines.Should().BeEmpty();
        log.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Live_lines_are_flushed_automatically_after_the_batch_interval()
    {
        var session = Substitute.For<IRunSession>();
        session.GetLines(Arg.Any<long>()).Returns([]);
        using var log = new RunLogViewModel(ImmediateDispatcher.Instance, TimeSpan.FromMilliseconds(10));
        log.Attach(session);

        session.LineReceived += Raise.Event<EventHandler<RunLogLine>>(session, Line(0, "hello"));

        for (var i = 0; i < 200 && log.Lines.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        log.Lines.Select(l => l.Text).Should().Equal("hello");
    }

    [Fact]
    public void A_truncated_buffer_is_flagged()
    {
        var session = Substitute.For<IRunSession>();
        session.GetLines(Arg.Any<long>()).Returns([Line(500, "late line")]);
        var log = new RunLogViewModel(ImmediateDispatcher.Instance, Timeout.InfiniteTimeSpan);

        log.Attach(session);

        log.IsTruncated.Should().BeTrue();
    }

    [Theory]
    [InlineData("error CS1002: ; expected", true)]
    [InlineData("npm ERR! code ELIFECYCLE", true)]
    [InlineData("FAILED tests/test_api.py::test_login", true)]
    [InlineData("thread 'main' panicked at src/main.rs:4:5", true)]
    [InlineData("Unhandled exception. System.InvalidOperationException", true)]
    [InlineData("    0 Error(s)", false)]
    [InlineData("Tests: 12 passed, 0 failed", false)]
    [InlineData("Build succeeded.", false)]
    [InlineData("Compiling forge v0.1.0", false)]
    public void Error_lines_are_recognized(string text, bool expected) => LogPatterns.IsError(text).Should().Be(expected);

    // ----- Command editor ----------------------------------------------------------------

    [Fact]
    public void Editor_validates_every_field()
    {
        using var folder = new TestFolder();
        var editor = new CommandEditorDialogViewModel(folder.Path) { Name = " ", CommandLine = "a\nb", WorkingDirectory = "../outside" };

        editor.Validate().Should().BeFalse();

        editor.NameError.Should().NotBeNull();
        editor.CommandLineError.Should().Contain("one line");
        editor.WorkingDirectoryError.Should().Contain("inside the project");
    }

    [Fact]
    public void Editor_rejects_absolute_and_missing_directories_and_accepts_existing_ones()
    {
        using var folder = new TestFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "src", "web"));
        var editor = new CommandEditorDialogViewModel(folder.Path) { Name = "Web", CommandLine = "npm start", WorkingDirectory = "C:/elsewhere" };
        editor.Validate().Should().BeFalse();
        editor.WorkingDirectoryError.Should().Contain("relative");

        editor.WorkingDirectory = "src/missing";
        editor.Validate().Should().BeFalse();
        editor.WorkingDirectoryError.Should().Contain("doesn't exist");

        editor.WorkingDirectory = @".\src\web\";
        editor.Validate().Should().BeTrue();
        editor.ResultWorkingDirectory.Should().Be("src/web");
        editor.WorkingDirectoryHint.Should().Be("Runs from src/web.");
    }

    [Fact]
    public void Editor_confirms_only_valid_input_and_reports_the_result()
    {
        using var folder = new TestFolder();
        var editor = new CommandEditorDialogViewModel(folder.Path, category: CommandCategory.Test);
        bool? result = null;
        editor.CloseRequested += (_, r) => result = r;

        editor.ConfirmCommand.Execute(null);
        result.Should().BeNull();

        editor.Name = "Unit tests";
        editor.CommandLine = "  dotnet test  ";
        editor.ConfirmCommand.Execute(null);

        result.Should().BeTrue();
        editor.ResultName.Should().Be("Unit tests");
        editor.ResultCommandLine.Should().Be("dotnet test");
        editor.ResultCategory.Should().Be(CommandCategory.Test);
        editor.ResultWorkingDirectory.Should().BeNull();
        editor.Title.Should().Be("Add command");
    }

    [Fact]
    public void Category_order_and_names()
    {
        CommandCategories.Ordered.Should().Equal(
            CommandCategory.Dev, CommandCategory.Build, CommandCategory.Test, CommandCategory.Lint, CommandCategory.Format, CommandCategory.Package,
            CommandCategory.Install, CommandCategory.Run, CommandCategory.Deploy, CommandCategory.Clean, CommandCategory.Other);
        CommandCategories.TryParse("build").Should().Be(CommandCategory.Build);
        CommandCategories.TryParse("abc123").Should().BeNull();
    }
}
