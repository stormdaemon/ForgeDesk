using ForgeDesk.Presentation.Palette;

namespace ForgeDesk.Presentation.Tests.Palette;

public class FuzzyMatcherTests
{
    [Fact]
    public void Characters_must_appear_in_order()
    {
        FuzzyMatcher.Match("psh", "Push").Should().NotBeNull();
        FuzzyMatcher.Match("hsp", "Push").Should().BeNull();
        FuzzyMatcher.Match("pushes", "Push").Should().BeNull();
        FuzzyMatcher.Match("x", null).Should().BeNull();
    }

    [Fact]
    public void Matching_ignores_case()
    {
        var match = FuzzyMatcher.Match("OPEN", "Open in Explorer");

        match.Should().NotBeNull();
        match!.Indices.Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public void Word_starts_are_preferred_over_letters_inside_words()
    {
        var match = FuzzyMatcher.Match("oe", "Open in Explorer")!;

        match.Indices.Should().Equal(0, 8);
    }

    [Fact]
    public void CamelCase_humps_count_as_word_starts()
    {
        var match = FuzzyMatcher.Match("pwv", "ProjectWorkspaceView")!;

        match.Indices.Should().Equal(0, 7, 16);
    }

    [Fact]
    public void Prefix_beats_a_match_in_the_middle()
    {
        var prefix = FuzzyMatcher.Match("git", "Git status")!.Score;
        var middle = FuzzyMatcher.Match("git", "Open digital")!.Score;

        prefix.Should().BeGreaterThan(middle);
    }

    [Fact]
    public void Contiguous_matches_beat_scattered_ones()
    {
        var contiguous = FuzzyMatcher.Match("set", "Settings")!.Score;
        var scattered = FuzzyMatcher.Match("set", "Show recent tasks")!.Score;

        contiguous.Should().BeGreaterThan(scattered);
    }

    [Fact]
    public void Exact_match_scores_highest()
    {
        var exact = FuzzyMatcher.Match("push", "Push")!.Score;
        var longer = FuzzyMatcher.Match("push", "Push tags")!.Score;

        exact.Should().BeGreaterThan(longer);
    }

    [Fact]
    public void Path_segments_count_as_word_starts()
    {
        var match = FuzzyMatcher.Match("scm", "src/core/main.rs")!;

        match.Indices.Should().Equal(0, 4, 9);
    }
}
