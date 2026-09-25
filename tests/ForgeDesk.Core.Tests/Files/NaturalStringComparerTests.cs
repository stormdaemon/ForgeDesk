using ForgeDesk.Core.Files;

namespace ForgeDesk.Core.Tests.Files;

public class NaturalStringComparerTests
{
    [Fact]
    public void Sorts_numbers_by_value_and_ignores_case()
    {
        string[] names = ["file10.txt", "File2.txt", "file1.txt", "readme.md", "Apple", "file01.txt", "b", "a10b", "a9c"];

        var sorted = names.Order(NaturalStringComparer.Instance).ToList();

        sorted.Should().Equal("a9c", "a10b", "Apple", "b", "file01.txt", "file1.txt", "File2.txt", "file10.txt", "readme.md");
    }

    [Fact]
    public void Handles_huge_digit_runs_without_overflow()
    {
        NaturalStringComparer.Instance.Compare("v99999999999999999999999", "v100000000000000000000000").Should().BeNegative();
    }

    [Fact]
    public void Is_consistent_for_equal_and_prefix_strings()
    {
        var comparer = NaturalStringComparer.Instance;
        comparer.Compare("abc", "abc").Should().Be(0);
        comparer.Compare("abc", "abcd").Should().BeNegative();
        comparer.Compare("abcd", "abc").Should().BePositive();
        comparer.Compare(null, "a").Should().BeNegative();
        comparer.Compare("A", "a").Should().NotBe(0, "ties are broken ordinally to stay deterministic");
    }
}
