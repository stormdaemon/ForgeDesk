using ForgeDesk.Core.Analysis;

namespace ForgeDesk.Core.Tests.Analysis;

public class TodoScannerTests
{
    [Theory]
    [InlineData("// TODO: handle retries", (int)(CommentSyntax.CLike), "TODO", "handle retries")]
    [InlineData("    foo(); // FIXME(ana): leaks on error", (int)(CommentSyntax.CLike), "FIXME", "leaks on error")]
    [InlineData("/* HACK - works around a CLR bug */", (int)(CommentSyntax.CLike), "HACK", "works around a CLR bug")]
    [InlineData(" * XXX: remove after migration", (int)(CommentSyntax.CLike), "XXX", "remove after migration")]
    [InlineData("/// BUG: wrong on leap years", (int)(CommentSyntax.CLike), "BUG", "wrong on leap years")]
    [InlineData("// todo refactor this", (int)(CommentSyntax.CLike), "TODO", "refactor this")]
    [InlineData("# TODO: python style", (int)(CommentSyntax.Hash), "TODO", "python style")]
    [InlineData("## FIXME", (int)(CommentSyntax.Hash), "FIXME", "")]
    [InlineData("-- TODO: index this column", (int)(CommentSyntax.DoubleDash), "TODO", "index this column")]
    [InlineData("<!-- TODO: translate -->", (int)(CommentSyntax.Markup), "TODO", "translate")]
    [InlineData("@* TODO: razor comment *@", (int)(CommentSyntax.Markup | CommentSyntax.CLike), "TODO", "razor comment")]
    [InlineData("; TODO lisp", (int)(CommentSyntax.Semicolon), "TODO", "lisp")]
    [InlineData("% TODO erlang", (int)(CommentSyntax.Percent), "TODO", "erlang")]
    [InlineData("' TODO: vb", (int)(CommentSyntax.Apostrophe), "TODO", "vb")]
    [InlineData("REM TODO: batch", (int)(CommentSyntax.Rem), "TODO", "batch")]
    [InlineData(":: FIXME batch", (int)(CommentSyntax.Rem), "FIXME", "batch")]
    [InlineData("// @TODO: annotated", (int)(CommentSyntax.CLike), "TODO", "annotated")]
    [InlineData("// TODO: windows line\r", (int)(CommentSyntax.CLike), "TODO", "windows line")]
    public void Finds_markers_at_the_start_of_comments(string line, int syntax, string tag, string text)
    {
        var match = TodoScanner.Match(line, (CommentSyntax)syntax);

        match.Should().NotBeNull();
        match!.Value.Tag.Should().Be(tag);
        match.Value.Text.Should().Be(text);
    }

    [Theory]
    [InlineData("var todoList = new List<Todo>();", (int)(CommentSyntax.CLike))]
    [InlineData("// Fix the bug in the parser", (int)(CommentSyntax.CLike))]
    [InlineData("// This is a hack, sorry", (int)(CommentSyntax.CLike))]
    [InlineData("// TODOS are tracked elsewhere", (int)(CommentSyntax.CLike))]
    [InlineData("// Format: XXX-XXX", (int)(CommentSyntax.CLike))]
    [InlineData("TODO: not in a comment", (int)(CommentSyntax.CLike))]
    [InlineData("# TODO: hash in a C file", (int)(CommentSyntax.CLike))]
    [InlineData("// TODO: comment syntax unknown", (int)(CommentSyntax.None))]
    [InlineData("x = 1 # bug fix", (int)(CommentSyntax.Hash))]
    public void Ignores_prose_identifiers_and_foreign_comment_syntax(string line, int syntax) =>
        TodoScanner.Match(line, (CommentSyntax)syntax).Should().BeNull();

    [Fact]
    public void Clips_long_texts()
    {
        var match = TodoScanner.Match("// TODO: " + new string('a', 500), CommentSyntax.CLike);

        match!.Value.Text.Should().HaveLength(AnalysisLimits.MaxTodoTextLength).And.EndWith("…");
    }

    [Fact]
    public void Ignores_very_long_lines() =>
        TodoScanner.Match("// TODO " + new string('x', 5000), CommentSyntax.CLike).Should().BeNull();
}
