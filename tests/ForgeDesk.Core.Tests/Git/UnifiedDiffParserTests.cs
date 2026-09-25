using System.Text;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Core.Tests.Git;

public class UnifiedDiffParserTests
{
    private static FileDiff ParseSingle(string diff, int maxLines = UnifiedDiffParser.DefaultMaxLines) =>
        UnifiedDiffParser.Parse(Encoding.UTF8.GetBytes(diff), maxLines).Should().ContainSingle().Subject;

    [Fact]
    public void Parses_hunks_with_line_numbers()
    {
        var diff = ParseSingle("""
            diff --git a/src/app.cs b/src/app.cs
            index 83db48f..bf269f4 100644
            --- a/src/app.cs
            +++ b/src/app.cs
            @@ -1,4 +1,5 @@ namespace App
             line 1
            -line 2
            +line two
            +line 2.5
             line 3
             line 4
            @@ -10,2 +11,2 @@
             line 10
            -line 11
            +line eleven

            """);

        diff.Path.Should().Be("src/app.cs");
        diff.OldPath.Should().BeNull();
        diff.HeaderLines.Should().HaveCount(4);
        diff.Hunks.Should().HaveCount(2);
        var first = diff.Hunks[0];
        first.Header.Should().Be("@@ -1,4 +1,5 @@ namespace App");
        (first.OldStart, first.OldCount, first.NewStart, first.NewCount).Should().Be((1, 4, 1, 5));
        first.Lines.Select(l => (l.Kind, l.Text, l.OldLineNumber, l.NewLineNumber)).Should().Equal(
            (DiffLineKind.Context, "line 1", 1, 1),
            (DiffLineKind.Removed, "line 2", 2, (int?)null),
            (DiffLineKind.Added, "line two", (int?)null, 2),
            (DiffLineKind.Added, "line 2.5", (int?)null, 3),
            (DiffLineKind.Context, "line 3", 3, 4),
            (DiffLineKind.Context, "line 4", 4, 5));
        diff.Hunks[1].Lines[2].NewLineNumber.Should().Be(12);
        diff.Additions.Should().Be(3);
        diff.Deletions.Should().Be(2);
    }

    [Fact]
    public void Content_that_looks_like_headers_stays_content()
    {
        var diff = ParseSingle("""
            diff --git a/notes.md b/notes.md
            --- a/notes.md
            +++ b/notes.md
            @@ -1,2 +1,2 @@
            --- a/fake
            ++++ b/fake
             diff --git a/x b/x

            """);

        diff.Hunks.Should().ContainSingle();
        diff.Hunks[0].Lines.Select(l => (l.Kind, l.Text)).Should().Equal(
            (DiffLineKind.Removed, "-- a/fake"),
            (DiffLineKind.Added, "+++ b/fake"),
            (DiffLineKind.Context, "diff --git a/x b/x"));
    }

    [Fact]
    public void No_newline_markers_are_kept_in_place()
    {
        var diff = ParseSingle("diff --git a/f b/f\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n");

        diff.Hunks[0].Lines.Select(l => l.Kind).Should().Equal(
            DiffLineKind.Removed, DiffLineKind.NoNewlineMarker, DiffLineKind.Added, DiffLineKind.NoNewlineMarker);
        diff.Hunks[0].Lines[1].Text.Should().Be("No newline at end of file");
        diff.Hunks[0].Lines[1].OldLineNumber.Should().BeNull();
    }

    [Fact]
    public void Crlf_lines_are_flagged_and_stripped_from_text()
    {
        var diff = ParseSingle("diff --git a/w.bat b/w.bat\n--- a/w.bat\n+++ b/w.bat\n@@ -1,2 +1,2 @@\n echo on\r\n-rem old\r\n+rem new\n");

        diff.Hunks[0].Lines.Select(l => (l.Text, l.HasCarriageReturn)).Should().Equal(("echo on", true), ("rem old", true), ("rem new", false));
    }

    [Fact]
    public void New_deleted_and_binary_files()
    {
        var files = UnifiedDiffParser.Parse(Encoding.UTF8.GetBytes("""
            diff --git a/new.txt b/new.txt
            new file mode 100644
            index 0000000..3b18e51
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1 @@
            +hello
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            index 3b18e51..0000000
            --- a/old.txt
            +++ /dev/null
            @@ -1 +0,0 @@
            -hello
            diff --git a/logo.png b/logo.png
            index 1111111..2222222 100644
            Binary files a/logo.png and b/logo.png differ

            """));

        files.Should().HaveCount(3);
        files[0].Should().Match<FileDiff>(f => f.Path == "new.txt" && f.IsNewFile && !f.IsDeletedFile && f.Hunks.Count == 1);
        files[0].Hunks[0].Lines[0].NewLineNumber.Should().Be(1);
        files[1].Should().Match<FileDiff>(f => f.Path == "old.txt" && f.IsDeletedFile && f.Deletions == 1);
        files[2].Should().Match<FileDiff>(f => f.Path == "logo.png" && f.IsBinary && f.Hunks.Count == 0);
    }

    [Fact]
    public void Renames_report_the_old_path()
    {
        // Git ends ---/+++ names containing spaces with a TAB.
        var diff = ParseSingle(
            "diff --git a/docs/old name.md b/docs/new name.md\nsimilarity index 90%\nrename from docs/old name.md\nrename to docs/new name.md\n" +
            "index 1111111..2222222 100644\n--- a/docs/old name.md\t\n+++ b/docs/new name.md\t\n@@ -1 +1 @@\n-a\n+b\n");

        diff.Path.Should().Be("docs/new name.md");
        diff.OldPath.Should().Be("docs/old name.md");
        diff.Hunks.Should().ContainSingle();
    }

    [Fact]
    public void Pure_rename_without_content_change()
    {
        var diff = ParseSingle("diff --git a/a.txt b/b.txt\nsimilarity index 100%\nrename from a.txt\nrename to b.txt\n");

        diff.Path.Should().Be("b.txt");
        diff.OldPath.Should().Be("a.txt");
        diff.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void Quoted_and_unicode_paths()
    {
        var quoted = ParseSingle("diff --git \"a/tab\\there.txt\" \"b/tab\\there.txt\"\nnew file mode 100644\n--- /dev/null\n+++ \"b/tab\\there.txt\"\n@@ -0,0 +1 @@\n+x\n");
        var unicode = ParseSingle("diff --git a/dossier/été.txt b/dossier/été.txt\n--- a/dossier/été.txt\n+++ b/dossier/été.txt\n@@ -1 +1 @@\n-a\n+b\n");
        var octal = ParseSingle("diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"\n--- \"a/caf\\303\\251.txt\"\n+++ \"b/caf\\303\\251.txt\"\n@@ -1 +1 @@\n-a\n+b\n");

        quoted.Path.Should().Be("tab\there.txt");
        unicode.Path.Should().Be("dossier/été.txt");
        octal.Path.Should().Be("café.txt");
    }

    [Fact]
    public void Symmetric_header_with_spaces_is_split_correctly_without_side_lines()
    {
        var diff = ParseSingle("diff --git a/my b/file.txt b/my b/file.txt\nold mode 100644\nnew mode 100755\n");

        diff.Path.Should().Be("my b/file.txt");
    }

    [Fact]
    public void Combined_diff_of_a_conflict_is_readable()
    {
        var diff = ParseSingle("""
            diff --cc f.txt
            index af70335,ac05874..0000000
            --- a/f.txt
            +++ b/f.txt
            @@@ -1,3 -1,3 +1,7 @@@
              a
            ++<<<<<<< HEAD
             +MAIN
            ++=======
            + FEAT
            ++>>>>>>> feat
              c

            """);

        diff.Path.Should().Be("f.txt");
        var lines = diff.Hunks.Should().ContainSingle().Subject.Lines;
        lines.Select(l => l.Kind).Should().Equal(
            DiffLineKind.Context, DiffLineKind.Added, DiffLineKind.Added, DiffLineKind.Added, DiffLineKind.Added, DiffLineKind.Added, DiffLineKind.Context);
        lines[1].Text.Should().Be("<<<<<<< HEAD");
        lines[^1].NewLineNumber.Should().Be(7);
    }

    [Fact]
    public void Too_many_lines_marks_the_file_too_large_without_hunks()
    {
        var builder = new StringBuilder("diff --git a/big.txt b/big.txt\n--- a/big.txt\n+++ b/big.txt\n@@ -0,0 +1,50 @@\n");
        for (var i = 0; i < 50; i++)
        {
            builder.Append("+line ").Append(i).Append('\n');
        }

        var diff = ParseSingle(builder.ToString(), maxLines: 10);

        diff.IsTooLarge.Should().BeTrue();
        diff.Hunks.Should().BeEmpty();
        diff.Path.Should().Be("big.txt");
    }

    [Fact]
    public void Invalid_utf8_is_replaced_rather_than_failing()
    {
        var bytes = Encoding.ASCII.GetBytes("diff --git a/l.txt b/l.txt\n--- a/l.txt\n+++ b/l.txt\n@@ -1 +1 @@\n-caf").Concat(new byte[] { 0xE9, (byte)'\n' })
            .Concat(Encoding.ASCII.GetBytes("+cafe\n")).ToArray();

        var diff = UnifiedDiffParser.Parse(bytes).Single();

        diff.Hunks[0].Lines[0].Text.Should().Be("caf�");
    }

    [Fact]
    public void Empty_input_has_no_files() =>
        UnifiedDiffParser.Parse([]).Should().BeEmpty();
}
