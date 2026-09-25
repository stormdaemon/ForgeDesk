using ForgeDesk.Core.Runs;

namespace ForgeDesk.Core.Tests.Runs;

public class AnsiEscapesTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("\u001b[31mred\u001b[0m", "red")]
    [InlineData("\u001b[1;32m✓\u001b[39;22m passed", "✓ passed")]
    [InlineData("\u001b[2K\u001b[1Gnext", "next")]
    [InlineData("\u001b[?25lhidden cursor\u001b[?25h", "hidden cursor")]
    [InlineData("\u001b]0;window title\u0007visible", "visible")]
    [InlineData("\u001b]8;;https://example.com\u001b\\link\u001b]8;;\u001b\\", "link")]
    [InlineData("\u001b(Bcharset", "charset")]
    [InlineData("\u001b7saved\u001b8", "saved")]
    [InlineData("tab\tkept", "tab\tkept")]
    [InlineData("bell\u0007gone", "bellgone")]
    [InlineData("\u009b31mc1 csi", "c1 csi")]
    public void Removes_escape_sequences_and_control_characters(string input, string expected)
    {
        AnsiEscapes.Strip(input).Should().Be(expected);
    }

    [Fact]
    public void Backspaces_erase_the_previous_character_like_spinners_expect()
    {
        AnsiEscapes.Strip("Installing ⠋\b⠙\b⠹").Should().Be("Installing ⠹");
    }

    [Fact]
    public void Unterminated_sequences_do_not_throw()
    {
        AnsiEscapes.Strip("text\u001b").Should().Be("text");
        AnsiEscapes.Strip("text\u001b[").Should().Be("text");
        AnsiEscapes.Strip("text\u001b]0;never terminated").Should().Be("text");
    }

    [Fact]
    public void Returns_the_same_instance_when_nothing_to_strip()
    {
        const string text = "nothing to do here";
        AnsiEscapes.Strip(text).Should().BeSameAs(text);
    }
}
