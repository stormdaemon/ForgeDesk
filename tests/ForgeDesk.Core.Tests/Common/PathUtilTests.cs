using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Tests.Common;

public class PathUtilTests
{
    [Fact]
    public void ResolveUnder_rejects_traversal_outside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        var act = () => PathUtil.ResolveUnder(root, "../../etc/passwd");
        act.Should().Throw<ForgeException>().Which.Kind.Should().Be(ErrorKind.InvalidInput);
    }

    [Fact]
    public void ResolveUnder_accepts_forward_slash_relative_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        PathUtil.ResolveUnder(root, "src/app/main.cs").Should().Be(Path.Combine(root, "src", "app", "main.cs"));
    }

    [Fact]
    public void IsWithin_does_not_match_sibling_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");
        PathUtil.IsWithin(root, root + "-other").Should().BeFalse();
        PathUtil.IsWithin(root, Path.Combine(root, "a")).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    public void FormatBytes_is_human_readable(long bytes, string expected) =>
        PathUtil.FormatBytes(bytes).Should().Be(expected.Replace(".", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator));
}
