using ForgeDesk.Presentation.Overview;

namespace ForgeDesk.Presentation.Tests.Overview;

public sealed class ReadmeSummaryTests
{
    [Fact]
    public void Skips_front_matter_headings_badges_and_html_to_the_first_paragraph()
    {
        const string readme = """
            ---
            title: Forge
            ---
            <p align="center"><img src="logo.png" width="96"></p>
            <h1 align="center">Forge</h1>

            [![Build](https://ci/badge.svg)](https://ci) [![npm](https://npm/badge.svg)](https://npm)

            # Forge
            > Experimental

            **Forge** turns `git` and your scripts into a _single_ cockpit.
            See the [docs](https://forge.dev/docs) &amp; enjoy.

            ## Install
            npm i forge
            """;

        ReadmeSummary.Extract(readme).Should().Be("Forge turns git and your scripts into a single cockpit. See the docs & enjoy.");
    }

    [Fact]
    public void Ignores_code_fences_lists_and_setext_headings()
    {
        const string readme = """
            Forge
            =====

            ```bash
            make all
            ```

            - feature one
            - feature two

            The actual description.
            """;

        ReadmeSummary.Extract(readme).Should().Be("The actual description.");
    }

    [Fact]
    public void Long_paragraphs_are_cut_on_a_word_with_an_ellipsis()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 200));

        var summary = ReadmeSummary.Extract(text, maxLength: 50)!;

        summary.Length.Should().BeLessThanOrEqualTo(51);
        summary.Should().EndWith("…");
        summary.Should().NotContain("wor…");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# Only a title\n\n## And a section")]
    [InlineData("[![badge](x)](y)")]
    public void Returns_null_without_prose(string? readme) => ReadmeSummary.Extract(readme).Should().BeNull();
}
