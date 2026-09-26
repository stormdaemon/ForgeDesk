using System.Text;
using ForgeDesk.Core.Common;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public class GitPatchBuilderTests
{
    private static FileDiff Parse(string diff) => UnifiedDiffParser.Parse(Encoding.UTF8.GetBytes(diff)).Single();

    [Fact]
    public void Rebuilds_git_header_and_recounts_the_hunk()
    {
        var diff = Parse("diff --git a/f.txt b/f.txt\nindex 1111111..2222222 100644\n--- a/f.txt\n+++ b/f.txt\n@@ -1,3 +1,3 @@ ctx\n a\n-b\n+B\n c\n");
        var trimmed = diff.Hunks[0] with { Lines = diff.Hunks[0].Lines.Where(l => l.Kind != DiffLineKind.Added).ToList() };

        var patch = GitPatchBuilder.Build(diff, trimmed);

        patch.Should().Be("diff --git a/f.txt b/f.txt\nindex 1111111..2222222 100644\n--- a/f.txt\n+++ b/f.txt\n@@ -1,3 +1,2 @@\n a\n-b\n c\n");
    }

    [Fact]
    public void Keeps_carriage_returns_and_no_newline_markers()
    {
        var diff = Parse("diff --git a/w.bat b/w.bat\n--- a/w.bat\n+++ b/w.bat\n@@ -1,2 +1,2 @@\n echo on\r\n-old\r\n\\ No newline at end of file\n+new\r\n\\ No newline at end of file\n");

        var patch = GitPatchBuilder.Build(diff, diff.Hunks[0]);

        patch.Should().EndWith("@@ -1,2 +1,2 @@\n echo on\r\n-old\r\n\\ No newline at end of file\n+new\r\n\\ No newline at end of file\n");
    }

    [Fact]
    public void Renames_become_an_in_place_change_of_the_new_path()
    {
        var diff = Parse("diff --git a/old.txt b/new.txt\nsimilarity index 80%\nrename from old.txt\nrename to new.txt\nindex 1..2 100644\n--- a/old.txt\n+++ b/new.txt\n@@ -1 +1 @@\n-a\n+b\n");

        var patch = GitPatchBuilder.Build(diff, diff.Hunks[0]);

        patch.Should().StartWith("diff --git a/new.txt b/new.txt\n--- a/new.txt\n+++ b/new.txt\n@@ -1,1 +1,1 @@\n");
        patch.Should().NotContain("rename");
    }

    [Fact]
    public void New_files_keep_their_mode_and_dev_null_side()
    {
        var diff = Parse("diff --git a/run.sh b/run.sh\nnew file mode 100755\nindex 0000000..1111111\n--- /dev/null\n+++ b/run.sh\n@@ -0,0 +1,2 @@\n+#!/bin/sh\n+echo hi\n");

        var patch = GitPatchBuilder.Build(diff, diff.Hunks[0]);

        patch.Should().Contain("new file mode 100755\n").And.Contain("--- /dev/null\n").And.Contain("@@ -0,0 +1,2 @@\n");
    }

    [Fact]
    public void Refuses_binary_conflicted_and_non_utf8_content()
    {
        var binary = new FileDiff { Path = "a.png", IsBinary = true };
        var hunk = new DiffHunk { Header = "@@ -1 +1 @@", Lines = [new DiffLine(DiffLineKind.Added, "x", null, 1)] };
        var combined = Parse("diff --cc f.txt\n--- a/f.txt\n+++ b/f.txt\n@@@ -1,1 -1,1 +1,2 @@@\n++x\n  y\n");
        var latin1 = Parse("diff --git a/l.txt b/l.txt\n--- a/l.txt\n+++ b/l.txt\n@@ -1 +1 @@\n-a\n+caf�\n");
        var contextOnly = hunk with { Lines = [new DiffLine(DiffLineKind.Context, "x", 1, 1)] };

        Invoking(() => GitPatchBuilder.Build(binary, hunk)).Should().Throw<ForgeException>().Which.Kind.Should().Be(ErrorKind.InvalidInput);
        Invoking(() => GitPatchBuilder.Build(combined, combined.Hunks[0])).Should().Throw<ForgeException>().WithMessage("*conflicted*");
        Invoking(() => GitPatchBuilder.Build(latin1, latin1.Hunks[0])).Should().Throw<ForgeException>().WithMessage("*UTF-8*");
        Invoking(() => GitPatchBuilder.Build(latin1, contextOnly)).Should().Throw<ForgeException>().WithMessage("*no changes*");
    }

    [Fact]
    public void Builds_a_header_when_none_is_available()
    {
        var diff = new FileDiff { Path = "dir/my file.txt" };
        var hunk = new DiffHunk { Header = "@@ -3,1 +3,1 @@", OldStart = 3, NewStart = 3, Lines = [new DiffLine(DiffLineKind.Removed, "a", 3, null), new DiffLine(DiffLineKind.Added, "b", null, 3)] };

        var patch = GitPatchBuilder.Build(diff, hunk);

        patch.Should().Be("diff --git a/dir/my file.txt b/dir/my file.txt\n--- a/dir/my file.txt\t\n+++ b/dir/my file.txt\t\n@@ -3,1 +3,1 @@\n-a\n+b\n");
    }

    private static Action Invoking(Action action) => action;
}
