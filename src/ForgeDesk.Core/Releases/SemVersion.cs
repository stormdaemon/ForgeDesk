using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using ForgeDesk.Core.Common;

namespace ForgeDesk.Core.Releases;

/// <summary>
/// A Semantic Versioning 2.0.0 version ("1.4.0", "2.0.0-rc.1+build.7"), optionally written with a
/// leading "v" as in git tags. <see cref="CompareTo"/> implements SemVer precedence (build metadata
/// is ignored); <see cref="Equals(SemVersion?)"/> is exact and includes the build metadata.
/// </summary>
public sealed partial class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private SemVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease, IReadOnlyList<string> build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PrereleaseIdentifiers = prerelease;
        BuildIdentifiers = build;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Dot-separated pre-release identifiers ("rc", "1"), empty for a release.</summary>
    public IReadOnlyList<string> PrereleaseIdentifiers { get; }

    /// <summary>Dot-separated build metadata identifiers, ignored for precedence.</summary>
    public IReadOnlyList<string> BuildIdentifiers { get; }

    public string Prerelease => string.Join('.', PrereleaseIdentifiers);

    public string Build => string.Join('.', BuildIdentifiers);

    public bool IsPrerelease => PrereleaseIdentifiers.Count > 0;

    public static SemVersion Create(int major, int minor, int patch, string? prerelease = null, string? build = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        var text = string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}.{patch}")
            + (string.IsNullOrEmpty(prerelease) ? string.Empty : "-" + prerelease)
            + (string.IsNullOrEmpty(build) ? string.Empty : "+" + build);
        return TryParse(text, out var version)
            ? version
            : throw new ArgumentException($"'{text}' is not a valid semantic version.", nameof(prerelease));
    }

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Pattern().Match(text.Trim());
        if (!match.Success
            || !int.TryParse(match.Groups["major"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups["minor"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups["patch"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch, Split(match.Groups["pre"]), Split(match.Groups["build"]));
        return true;
    }

    /// <summary>Parses a version or tag name; throws an <see cref="ErrorKind.InvalidInput"/> error the user can read.</summary>
    public static SemVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new ForgeException(ErrorKind.InvalidInput,
                $"\"{text}\" is not a valid version.",
                "Use the MAJOR.MINOR.PATCH format, for example 1.4.0 or 2.0.0-beta.1.");

    /// <summary>Next major version. A pre-release of an x.0.0 version is released as is (2.0.0-rc.1 → 2.0.0).</summary>
    public SemVersion BumpMajor() =>
        IsPrerelease && Minor == 0 && Patch == 0
            ? new SemVersion(Major, 0, 0, [], [])
            : new SemVersion(checked(Major + 1), 0, 0, [], []);

    /// <summary>Next minor version. A pre-release of an x.y.0 version is released as is (1.3.0-beta → 1.3.0).</summary>
    public SemVersion BumpMinor() =>
        IsPrerelease && Patch == 0
            ? new SemVersion(Major, Minor, 0, [], [])
            : new SemVersion(Major, checked(Minor + 1), 0, [], []);

    /// <summary>Next patch version. A pre-release is released as is (1.2.3-beta → 1.2.3).</summary>
    public SemVersion BumpPatch() =>
        IsPrerelease
            ? new SemVersion(Major, Minor, Patch, [], [])
            : new SemVersion(Major, Minor, checked(Patch + 1), [], []);

    /// <summary>
    /// Next pre-release of the same version: the last numeric identifier is incremented
    /// (rc.1 → rc.2), otherwise ".1" is appended (beta → beta.1). A release gets "-rc.1" on its next patch.
    /// </summary>
    public SemVersion BumpPrerelease()
    {
        if (!IsPrerelease)
        {
            return new SemVersion(Major, Minor, checked(Patch + 1), ["rc", "1"], []);
        }

        var identifiers = PrereleaseIdentifiers.ToList();
        if (IsNumeric(identifiers[^1]) && long.TryParse(identifiers[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var last))
        {
            identifiers[^1] = (last + 1).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            identifiers.Add("1");
        }

        return new SemVersion(Major, Minor, Patch, identifiers, []);
    }

    /// <summary>The same version without pre-release and build metadata.</summary>
    public SemVersion WithoutMetadata() => new(Major, Minor, Patch, [], []);

    /// <summary>SemVer 2.0.0 precedence (§11): build metadata is ignored.</summary>
    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core == 0)
        {
            core = Minor.CompareTo(other.Minor);
        }

        if (core == 0)
        {
            core = Patch.CompareTo(other.Patch);
        }

        if (core != 0)
        {
            return core;
        }

        // A release has higher precedence than any of its pre-releases.
        if (!IsPrerelease || !other.IsPrerelease)
        {
            return other.IsPrerelease.CompareTo(IsPrerelease);
        }

        var count = Math.Min(PrereleaseIdentifiers.Count, other.PrereleaseIdentifiers.Count);
        for (var i = 0; i < count; i++)
        {
            var result = CompareIdentifiers(PrereleaseIdentifiers[i], other.PrereleaseIdentifiers[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return PrereleaseIdentifiers.Count.CompareTo(other.PrereleaseIdentifiers.Count);
    }

    public bool Equals(SemVersion? other) =>
        other is not null
        && Major == other.Major && Minor == other.Minor && Patch == other.Patch
        && PrereleaseIdentifiers.SequenceEqual(other.PrereleaseIdentifiers, StringComparer.Ordinal)
        && BuildIdentifiers.SequenceEqual(other.BuildIdentifiers, StringComparer.Ordinal);

    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease, Build);

    /// <summary>"1.2.3-beta.1+build.5".</summary>
    public override string ToString() => ToString(withPrefix: false);

    /// <summary>With <paramref name="withPrefix"/>, the git tag form "v1.2.3".</summary>
    public string ToString(bool withPrefix)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (IsPrerelease)
        {
            text += "-" + Prerelease;
        }

        if (BuildIdentifiers.Count > 0)
        {
            text += "+" + Build;
        }

        return withPrefix ? "v" + text : text;
    }

    public static bool operator ==(SemVersion? left, SemVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemVersion? left, SemVersion? right) => !(left == right);

    public static bool operator <(SemVersion? left, SemVersion? right) => Compare(left, right) < 0;

    public static bool operator <=(SemVersion? left, SemVersion? right) => Compare(left, right) <= 0;

    public static bool operator >(SemVersion? left, SemVersion? right) => Compare(left, right) > 0;

    public static bool operator >=(SemVersion? left, SemVersion? right) => Compare(left, right) >= 0;

    /// <summary>Precedence comparison where null sorts first.</summary>
    public static int Compare(SemVersion? left, SemVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        return (leftNumeric, rightNumeric) switch
        {
            // Numeric identifiers have no leading zeros, so a longer one is larger (no overflow on huge values).
            (true, true) => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right),
            (true, false) => -1,
            (false, true) => 1,
            _ => Math.Sign(string.CompareOrdinal(left, right)),
        };
    }

    private static bool IsNumeric(string identifier) => identifier.All(char.IsAsciiDigit);

    private static string[] Split(Group group) => group.Success ? group.Value.Split('.') : [];

    // The official SemVer 2.0.0 grammar, plus an optional "v" prefix.
    [GeneratedRegex(
        @"^[vV]?(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)"
        + @"(?:-(?<pre>(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
        + @"(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
