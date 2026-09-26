using System.Diagnostics;
using ForgeDesk.App.Controls.Diff;
using ForgeDesk.Core.Git;

namespace ForgeDesk.Presentation.Tests.AppLogic;

public class DiffRowBuilderTests
{
    private static DiffHunk Hunk(string header, params DiffLine[] lines) => new() { Header = header, Lines = lines };

    private static FileDiff Diff(params DiffHunk[] hunks) => new() { Path = "src/app.cs", Hunks = hunks };

    [Fact]
    public void Each_hunk_starts_with_its_header_row()
    {
        var first = Hunk("@@ -1,2 +1,2 @@", new DiffLine(DiffLineKind.Context, "a", 1, 1), new DiffLine(DiffLineKind.Removed, "b", 2, null),
            new DiffLine(DiffLineKind.Added, "c", null, 2));
        var second = Hunk("@@ -10 +10 @@ void Main()", new DiffLine(DiffLineKind.Added, "d", null, 10));

        var rows = DiffRowBuilder.Build(Diff(first, second)).Rows;

        rows.Select(r => r.Kind).Should().Equal(
            DiffRowKind.HunkHeader, DiffRowKind.Context, DiffRowKind.Removed, DiffRowKind.Added,
            DiffRowKind.HunkHeader, DiffRowKind.Added);
        rows[0].Hunk.Should().BeSameAs(first);
        rows[3].Hunk.Should().BeSameAs(first);
        rows[4].Text.Should().Be("@@ -10 +10 @@ void Main()");
        rows[5].Hunk.Should().BeSameAs(second);
        rows.Select(r => r.Index).Should().Equal(0, 1, 2, 3, 4, 5);
    }

    [Fact]
    public void Line_numbers_and_markers_are_precomputed()
    {
        var rows = DiffRowBuilder.Build(Diff(Hunk("@@ -7,2 +7,2 @@",
            new DiffLine(DiffLineKind.Context, "same", 7, 7),
            new DiffLine(DiffLineKind.Removed, "old", 8, null),
            new DiffLine(DiffLineKind.Added, "new", null, 8)))).Rows;

        rows[1].Should().BeEquivalentTo(new { OldNumber = "7", NewNumber = "7", Marker = " ", Text = "same" });
        rows[2].Should().BeEquivalentTo(new { OldNumber = "8", NewNumber = "", Marker = "-", Text = "old" });
        rows[3].Should().BeEquivalentTo(new { OldNumber = "", NewNumber = "8", Marker = "+", Text = "new" });
        rows[0].Should().BeEquivalentTo(new { OldNumber = "", NewNumber = "", Marker = "", IsHunkHeader = true });
    }

    [Fact]
    public void No_newline_marker_reads_naturally()
    {
        var rows = DiffRowBuilder.Build(Diff(Hunk("@@ -1 +1 @@",
            new DiffLine(DiffLineKind.Added, "x", null, 1),
            new DiffLine(DiffLineKind.NoNewlineMarker, "\\ No newline at end of file", null, null)))).Rows;

        rows[2].Kind.Should().Be(DiffRowKind.NoNewline);
        rows[2].Text.Should().Be("No newline at end of file");
        rows[2].Marker.Should().BeEmpty();
    }

    [Theory]
    [InlineData("line\r", "line")]
    [InlineData("\tindented", "    indented")]
    [InlineData("ab\tc", "ab  c")]
    [InlineData("abcd\te", "abcd    e")]
    [InlineData("", "")]
    public void Text_is_cleaned_for_display(string raw, string expected) => DiffRowBuilder.Clean(raw).Should().Be(expected);

    [Fact]
    public void Very_long_lines_are_shortened()
    {
        var cleaned = DiffRowBuilder.Clean(new string('x', DiffRowBuilder.MaxDisplayedLineLength + 500));

        cleaned.Should().HaveLength(DiffRowBuilder.MaxDisplayedLineLength + 1).And.EndWith("…");
    }

    [Fact]
    public void Line_at_the_limit_is_kept_whole() =>
        DiffRowBuilder.Clean(new string('x', DiffRowBuilder.MaxDisplayedLineLength)).Should().NotEndWith("…");

    [Fact]
    public void Measurements_cover_the_longest_line_and_largest_number()
    {
        var result = DiffRowBuilder.Build(Diff(Hunk("@@ -99998,3 +99998,3 @@",
            new DiffLine(DiffLineKind.Context, "short", 99998, 99998),
            new DiffLine(DiffLineKind.Added, new string('y', 150), null, 100001))));

        result.LongestLine.Should().Be(150);
        result.LineNumberDigits.Should().Be(6);
    }

    [Fact]
    public void Small_files_reserve_three_digits() =>
        DiffRowBuilder.Build(Diff(Hunk("@@ -1 +1 @@", new DiffLine(DiffLineKind.Added, "a", null, 1)))).LineNumberDigits.Should().Be(3);

    [Fact]
    public void Empty_diff_has_no_rows() => DiffRowBuilder.Build(Diff()).Rows.Should().BeEmpty();

    [Fact]
    public void Twenty_thousand_lines_build_quickly()
    {
        var lines = Enumerable.Range(1, 20_000)
            .Select(i => new DiffLine(i % 3 == 0 ? DiffLineKind.Added : DiffLineKind.Context, $"\tline {i} with some code();", i, i))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();

        var result = DiffRowBuilder.Build(Diff(Hunk("@@ -1,20000 +1,20000 @@", lines)));

        stopwatch.Stop();
        result.Rows.Should().HaveCount(20_001);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
