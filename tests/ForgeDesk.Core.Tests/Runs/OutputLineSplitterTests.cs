using System.Text;
using ForgeDesk.Core.Runs;

namespace ForgeDesk.Core.Tests.Runs;

public class OutputLineSplitterTests
{
    private static List<OutputSegment> Split(params string[] chunks) => SplitBytes(chunks.Select(Encoding.UTF8.GetBytes).ToArray());

    private static List<OutputSegment> SplitBytes(params byte[][] chunks)
    {
        var splitter = new OutputLineSplitter();
        var output = new List<OutputSegment>();
        foreach (var chunk in chunks)
        {
            splitter.Feed(chunk, output);
        }

        splitter.Complete(output);
        return output;
    }

    private static List<string> Lines(List<OutputSegment> segments) => segments.Where(s => !s.IsTransient).Select(s => s.Text).ToList();

    [Fact]
    public void Splits_on_lf_and_crlf()
    {
        Lines(Split("one\ntwo\r\nthree\n")).Should().Equal("one", "two", "three");
    }

    [Fact]
    public void Keeps_blank_lines()
    {
        Lines(Split("a\n\nb\n")).Should().Equal("a", "", "b");
    }

    [Fact]
    public void Joins_lines_split_across_reads()
    {
        Lines(Split("hel", "lo wor", "ld\nnext", " line\n")).Should().Equal("hello world", "next line");
    }

    [Fact]
    public void Crlf_split_across_reads_is_a_single_line_break()
    {
        Lines(Split("first\r", "\nsecond\n")).Should().Equal("first", "second");
    }

    [Fact]
    public void Flushes_a_last_line_without_newline()
    {
        Lines(Split("no newline at end")).Should().Equal("no newline at end");
    }

    [Fact]
    public void Carriage_return_redraws_keep_only_the_final_state()
    {
        var segments = Split("Downloading 10%\rDownloading 50%\rDownloading 100%\ndone\n");

        Lines(segments).Should().Equal("Downloading 100%", "done");
        segments.Where(s => s.IsTransient).Select(s => s.Text).Should().Equal("Downloading 10%", "Downloading 50%");
    }

    [Fact]
    public void A_redraw_followed_by_an_empty_line_keeps_the_redrawn_text()
    {
        Lines(Split("progress 100%\r\u001b[K\nnext\n")).Should().Equal("progress 100%", "next");
    }

    [Fact]
    public void Cargo_style_clear_then_write_keeps_the_new_line()
    {
        Lines(Split("   Building [==>   ] 3/10: foo\r\u001b[K   Compiling bar v1.0\n")).Should().Equal("   Compiling bar v1.0");
    }

    [Fact]
    public void Leading_carriage_returns_are_handled()
    {
        Lines(Split("\r 1%\r 2%\r 3%\n")).Should().Equal(" 3%");
    }

    [Fact]
    public void Output_ending_with_a_redraw_keeps_it()
    {
        Lines(Split("step 1\rstep 2\r")).Should().Equal("step 2");
    }

    [Fact]
    public void Strips_escape_sequences_and_byte_order_mark()
    {
        Lines(Split("﻿\u001b[32mgreen\u001b[0m\n")).Should().Equal("green");
    }

    [Fact]
    public void Multibyte_characters_split_across_reads_are_decoded()
    {
        var bytes = Encoding.UTF8.GetBytes("héllo wörld ✓\n");
        var chunks = bytes.Select(b => new[] { b }).ToArray();
        Lines(SplitBytes(chunks)).Should().Equal("héllo wörld ✓");
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_a_legacy_code_page()
    {
        // "café" in Latin-1 / the OEM code pages: 0xE9 alone is not valid UTF-8.
        var line = Lines(SplitBytes([0x63, 0x61, 0x66, 0xE9, 0x0A])).Single();
        line.Should().StartWith("caf").And.HaveLength(4);
        line.Should().NotContain("�");
    }

    [Fact]
    public void Very_long_lines_are_wrapped_without_splitting_characters()
    {
        var splitter = new OutputLineSplitter(maxLineBytes: 32);
        var output = new List<OutputSegment>();
        var text = string.Concat(Enumerable.Repeat("aé", 40));
        splitter.Feed(Encoding.UTF8.GetBytes(text + "\n"), output);
        splitter.Complete(output);

        output.Should().HaveCountGreaterThan(2);
        output.Should().OnlyContain(s => !s.IsTransient && !s.Text.Contains('�'));
        string.Concat(output.Select(s => s.Text)).Should().Be(text);
    }
}
