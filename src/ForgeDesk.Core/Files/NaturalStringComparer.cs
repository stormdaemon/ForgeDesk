namespace ForgeDesk.Core.Files;

/// <summary>
/// Explorer-like ordering: case-insensitive, and digit runs compared by value so "file2"
/// sorts before "file10". Ties fall back to ordinal comparison to stay deterministic.
/// </summary>
internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var result = CompareNumbers(x, ref i, y, ref j);
                if (result != 0)
                {
                    return result;
                }

                continue;
            }

            var a = char.ToUpperInvariant(x[i]);
            var b = char.ToUpperInvariant(y[j]);
            if (a != b)
            {
                return a.CompareTo(b);
            }

            i++;
            j++;
        }

        var lengthOrder = (x.Length - i).CompareTo(y.Length - j);
        return lengthOrder != 0 ? lengthOrder : string.CompareOrdinal(x, y);
    }

    /// <summary>Compares digit runs by value; equal values ("01", "1") are left to the final ordinal tie-break.</summary>
    private static int CompareNumbers(string x, ref int i, string y, ref int j)
    {
        // Skip leading zeros, then the longer digit run is the bigger number.
        while (i < x.Length && x[i] == '0')
        {
            i++;
        }

        while (j < y.Length && y[j] == '0')
        {
            j++;
        }

        var digitsX = i;
        var digitsY = j;
        while (i < x.Length && char.IsAsciiDigit(x[i]))
        {
            i++;
        }

        while (j < y.Length && char.IsAsciiDigit(y[j]))
        {
            j++;
        }

        var lengthX = i - digitsX;
        var lengthY = j - digitsY;
        if (lengthX != lengthY)
        {
            return lengthX.CompareTo(lengthY);
        }

        return Math.Sign(string.CompareOrdinal(x, digitsX, y, digitsY, lengthX));
    }
}
