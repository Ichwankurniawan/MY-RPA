using System.Globalization;
using System.Text.RegularExpressions;

namespace MyRPA.Spreadsheet;

/// <summary>A rectangle of cells, 1-based and inclusive.</summary>
internal readonly record struct CellRange(int FirstRow, int FirstColumn, int LastRow, int LastColumn);

/// <summary>A1-style cell addresses: columns A..XFD (1..16384), rows 1..1048576.</summary>
internal static partial class CellAddress
{
    public const int MaxColumn = 16_384;

    public const int MaxRow = 1_048_576;

    /// <summary>Parses "B2" into column 2 and row 2.</summary>
    public static bool TryParse(string text, out int column, out int row)
    {
        column = 0;
        row = 0;
        var match = Cell().Match(text);
        if (!match.Success)
        {
            return false;
        }

        foreach (var c in match.Groups[1].Value.ToUpperInvariant())
        {
            column = (column * 26) + (c - 'A' + 1);
        }

        return int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out row)
            && column is >= 1 and <= MaxColumn && row is >= 1 and <= MaxRow;
    }

    /// <summary>Parses "A1:D100" (or one cell, "B2") into a range.</summary>
    public static bool TryParseRange(string text, out CellRange range)
    {
        range = default;
        var parts = text.Split(':');
        if (parts.Length is < 1 or > 2 || !TryParse(parts[0], out var c1, out var r1))
        {
            return false;
        }

        var (c2, r2) = (c1, r1);
        if (parts.Length == 2 && !TryParse(parts[1], out c2, out r2))
        {
            return false;
        }

        range = new CellRange(Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
        return true;
    }

    /// <summary>The column's letters: 1 → A, 28 → AB.</summary>
    public static string ColumnName(int column)
    {
        var name = string.Empty;
        for (; column > 0; column = (column - 1) / 26)
        {
            name = (char)('A' + ((column - 1) % 26)) + name;
        }

        return name;
    }

    [GeneratedRegex("^([A-Za-z]{1,3})([0-9]{1,7})$", RegexOptions.CultureInvariant)]
    private static partial Regex Cell();
}
