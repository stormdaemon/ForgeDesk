using ForgeDesk.Core.Common;
using ForgeDesk.Core.Files;

namespace ForgeDesk.Core.Tests.Files;

public class SearchPatternTests
{
    private static SearchPattern Create(string pattern, bool regex = false, bool matchCase = false, bool wholeWord = false) =>
        SearchPattern.Create(new ContentSearchQuery { Pattern = pattern, IsRegex = regex, MatchCase = matchCase, WholeWord = wholeWord });

    [Fact]
    public void Invalid_regex_is_rejected_with_the_parser_message()
    {
        var act = () => Create("foo(", regex: true);
        var error = act.Should().Throw<ForgeException>().Which;
        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Message.Should().StartWith("The search pattern is not a valid regular expression:");
        error.Message.Length.Should().BeGreaterThan("The search pattern is not a valid regular expression: ".Length);
    }

    [Fact]
    public void Empty_pattern_is_rejected()
    {
        var act = () => Create("");
        act.Should().Throw<ForgeException>().Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public void Literal_patterns_escape_regex_characters()
    {
        Create("a.b(c)").FindFirst("xx a.b(c) yy").Should().Be((3, 6));
        Create("a.b(c)").FindFirst("axb(c)").Should().BeNull();
    }

    [Fact]
    public void Case_and_whole_word_options_apply()
    {
        Create("Foo").FindFirst("a foo b").Should().Be((2, 3));
        Create("Foo", matchCase: true).FindFirst("a foo b").Should().BeNull();
        Create("foo", wholeWord: true).FindFirst("food foo_bar foo!").Should().Be((13, 3));
        Create("é", wholeWord: true).FindFirst("café é").Should().Be((5, 1));
    }

    [Fact]
    public void Creates_one_based_columns()
    {
        var match = Create(@"\d+", regex: true).CreateMatch("a.txt", 7, "abc 123\r");

        match.Should().Be(new ContentMatch("a.txt", 7, 5, 3, "abc 123"));
    }

    [Fact]
    public void Long_lines_are_cut_around_the_match()
    {
        var line = new string('x', 5_000) + "NEEDLE" + new string('y', 5_000);

        var match = Create("needle").CreateMatch("min.js", 1, line);

        match.LineText.Length.Should().BeLessThanOrEqualTo(SearchPattern.MaxLineTextLength + 2);
        match.LineText.Should().StartWith("…").And.EndWith("…");
        match.LineText.Substring(match.Column - 1, match.Length).Should().Be("NEEDLE");
    }

    [Theory]
    [InlineData("abc", 1, 0)]
    [InlineData("abc", 3, 2)]
    [InlineData("héllo", 4, 2)]
    [InlineData("✓ ok", 5, 2)]
    [InlineData("😀x", 5, 2)]
    public void Converts_git_byte_columns_to_character_indexes(string text, int byteColumn, int expectedIndex)
    {
        SearchPattern.ByteColumnToCharIndex(text, byteColumn).Should().Be(expectedIndex);
    }
}
