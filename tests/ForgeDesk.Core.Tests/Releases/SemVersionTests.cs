using ForgeDesk.Core.Common;
using ForgeDesk.Core.Releases;

namespace ForgeDesk.Core.Tests.Releases;

public class SemVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", "")]
    [InlineData("v1.2.3", 1, 2, 3, "", "")]
    [InlineData("V10.20.30", 10, 20, 30, "", "")]
    [InlineData("0.0.0", 0, 0, 0, "", "")]
    [InlineData(" 1.2.3 ", 1, 2, 3, "", "")]
    [InlineData("1.2.3-beta.1+build.5", 1, 2, 3, "beta.1", "build.5")]
    [InlineData("1.0.0-alpha-a.b-c-somethinglong+build.1-aef.1-its-okay", 1, 0, 0, "alpha-a.b-c-somethinglong", "build.1-aef.1-its-okay")]
    [InlineData("1.0.0+0.build.1-rc.10000aaa-kk-0.1", 1, 0, 0, "", "0.build.1-rc.10000aaa-kk-0.1")]
    [InlineData("1.0.0-0A.is.legal", 1, 0, 0, "0A.is.legal", "")]
    [InlineData("2147483647.0.0", int.MaxValue, 0, 0, "", "")]
    public void Parses_valid_versions(string text, int major, int minor, int patch, string prerelease, string build)
    {
        SemVersion.TryParse(text, out var version).Should().BeTrue();

        version!.Major.Should().Be(major);
        version.Minor.Should().Be(minor);
        version.Patch.Should().Be(patch);
        version.Prerelease.Should().Be(prerelease);
        version.Build.Should().Be(build);
        version.IsPrerelease.Should().Be(prerelease.Length > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-beta_1")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+build..1")]
    [InlineData("vv1.2.3")]
    [InlineData("release-1.2.3")]
    [InlineData("1.2.3 beta")]
    [InlineData("2147483648.0.0")]
    [InlineData("１.2.3")]
    [InlineData("-1.2.3")]
    public void Rejects_invalid_versions(string? text) => SemVersion.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Parse_throws_a_readable_error()
    {
        var act = () => SemVersion.Parse("1.2");

        var error = act.Should().Throw<ForgeException>().Which;
        error.Kind.Should().Be(ErrorKind.InvalidInput);
        error.Message.Should().Contain("1.2");
        error.Hint.Should().Contain("MAJOR.MINOR.PATCH");
    }

    // The ordering example of the SemVer 2.0.0 specification (§11), plus core version ordering.
    private static readonly string[] Ordered =
    [
        "0.9.9",
        "1.0.0-0.3.7",
        "1.0.0-alpha",
        "1.0.0-alpha.1",
        "1.0.0-alpha.beta",
        "1.0.0-beta",
        "1.0.0-beta.2",
        "1.0.0-beta.11",
        "1.0.0-rc.1",
        "1.0.0",
        "1.0.1",
        "1.1.0",
        "1.10.0",
        "2.0.0-x.7.z.92",
        "2.0.0",
        "2.1.0",
        "2.1.1",
        "10.0.0",
    ];

    [Fact]
    public void Precedence_follows_the_specification_for_every_pair()
    {
        var versions = Ordered.Select(SemVersion.Parse).ToList();

        for (var i = 0; i < versions.Count; i++)
        {
            for (var j = 0; j < versions.Count; j++)
            {
                var expected = i.CompareTo(j);
                Math.Sign(versions[i].CompareTo(versions[j])).Should().Be(expected, $"{versions[i]} vs {versions[j]}");
                (versions[i] < versions[j]).Should().Be(expected < 0);
                (versions[i] > versions[j]).Should().Be(expected > 0);
                (versions[i] <= versions[j]).Should().Be(expected <= 0);
                (versions[i] >= versions[j]).Should().Be(expected >= 0);
            }
        }
    }

    [Fact]
    public void Sorting_a_shuffled_list_restores_the_specification_order()
    {
        var shuffled = Ordered.Reverse().Where((_, i) => i % 2 == 0).Concat(Ordered.Where((_, i) => i % 2 == 0)).Distinct().ToList();
        shuffled.AddRange(Ordered.Except(shuffled));

        var sorted = shuffled.Select(SemVersion.Parse).Order().Select(v => v.ToString());

        sorted.Should().Equal(Ordered);
    }

    [Fact]
    public void Build_metadata_is_ignored_for_precedence_but_not_for_equality()
    {
        var a = SemVersion.Parse("1.0.0+build.1");
        var b = SemVersion.Parse("1.0.0+build.2");

        a.CompareTo(b).Should().Be(0);
        (a <= b && a >= b).Should().BeTrue();
        a.Equals(b).Should().BeFalse();
        (a == b).Should().BeFalse();
        (a == SemVersion.Parse("v1.0.0+build.1")).Should().BeTrue();
        a.GetHashCode().Should().Be(SemVersion.Parse("1.0.0+build.1").GetHashCode());
    }

    [Fact]
    public void Huge_numeric_prerelease_identifiers_compare_numerically()
    {
        var small = SemVersion.Parse("1.0.0-rc.99999999999999999999");
        var large = SemVersion.Parse("1.0.0-rc.100000000000000000000");

        (small < large).Should().BeTrue();
    }

    [Fact]
    public void Numeric_identifiers_sort_before_alphanumeric_ones()
    {
        (SemVersion.Parse("1.0.0-1") < SemVersion.Parse("1.0.0-a")).Should().BeTrue();
        (SemVersion.Parse("1.0.0-alpha.9") < SemVersion.Parse("1.0.0-alpha.10")).Should().BeTrue();
        (SemVersion.Parse("1.0.0-Beta") < SemVersion.Parse("1.0.0-alpha")).Should().BeTrue("identifiers compare in ASCII order");
    }

    [Fact]
    public void Null_sorts_first()
    {
        var version = SemVersion.Parse("0.0.1");

        version.CompareTo(null).Should().BePositive();
        SemVersion.Compare(null, version).Should().BeNegative();
        SemVersion.Compare(null, null).Should().Be(0);
        (version == null).Should().BeFalse();
        ((SemVersion?)null == null).Should().BeTrue();
    }

    [Theory]
    [InlineData("1.2.3", "2.0.0", "1.3.0", "1.2.4")]
    [InlineData("0.9.9", "1.0.0", "0.10.0", "0.9.10")]
    [InlineData("1.2.3+build.7", "2.0.0", "1.3.0", "1.2.4")]
    [InlineData("1.2.3-beta.1", "2.0.0", "1.3.0", "1.2.3")]
    [InlineData("1.3.0-rc.1", "2.0.0", "1.3.0", "1.3.0")]
    [InlineData("2.0.0-rc.1", "2.0.0", "2.0.0", "2.0.0")]
    public void Bumps_follow_semver_and_release_pending_prereleases(string from, string major, string minor, string patch)
    {
        var version = SemVersion.Parse(from);

        version.BumpMajor().ToString().Should().Be(major);
        version.BumpMinor().ToString().Should().Be(minor);
        version.BumpPatch().ToString().Should().Be(patch);
    }

    [Theory]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.2")]
    [InlineData("1.0.0-beta", "1.0.0-beta.1")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.beta.1")]
    [InlineData("1.0.0-9", "1.0.0-10")]
    [InlineData("1.2.3", "1.2.4-rc.1")]
    public void BumpPrerelease_increments_the_last_number(string from, string expected) =>
        SemVersion.Parse(from).BumpPrerelease().ToString().Should().Be(expected);

    [Fact]
    public void ToString_writes_the_canonical_form_with_or_without_prefix()
    {
        var version = SemVersion.Parse("v1.2.3-beta.1+sha.abc");

        version.ToString().Should().Be("1.2.3-beta.1+sha.abc");
        version.ToString(withPrefix: true).Should().Be("v1.2.3-beta.1+sha.abc");
        version.ToString(withPrefix: false).Should().Be("1.2.3-beta.1+sha.abc");
        version.WithoutMetadata().ToString(withPrefix: true).Should().Be("v1.2.3");
    }

    [Fact]
    public void Create_builds_and_validates_versions()
    {
        SemVersion.Create(1, 4, 0).ToString().Should().Be("1.4.0");
        SemVersion.Create(1, 4, 0, "rc.1", "exp").ToString().Should().Be("1.4.0-rc.1+exp");

        var invalid = () => SemVersion.Create(1, 0, 0, "bad..id");
        invalid.Should().Throw<ArgumentException>();
        var negative = () => SemVersion.Create(-1, 0, 0);
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }
}
