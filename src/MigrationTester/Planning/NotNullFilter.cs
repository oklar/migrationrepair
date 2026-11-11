using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MigrationTester.Planning;

/// <summary>
/// Recognizes index filters that only require columns to be non-NULL, such as the
/// <c>[Email] IS NOT NULL</c> EF Core generates for unique indexes on nullable columns in SQL Server.
/// </summary>
internal static partial class NotNullFilter
{
    /// <summary>
    /// True when <paramref name="filter"/> is one or more <c>[column] IS NOT NULL</c> or <c>"column" IS NOT NULL</c>
    /// terms joined by AND, optionally parenthesized as the database stores it, e.g.
    /// <c>([A] IS NOT NULL AND [B] IS NOT NULL)</c>.
    /// </summary>
    public static bool TryParse(string? filter, [NotNullWhen(true)] out IReadOnlyList<string>? columns)
    {
        columns = null;
        if (string.IsNullOrWhiteSpace(filter))
        {
            return false;
        }

        var found = new List<string>();
        foreach (string term in AndSeparator().Split(StripParentheses(filter)))
        {
            var match = IsNotNullTerm().Match(StripParentheses(term));
            if (!match.Success)
            {
                return false;
            }

            found.Add(
                match.Groups["bracketed"].Success ? match.Groups["bracketed"].Value.Replace("]]", "]")
                : match.Groups["quoted"].Success ? match.Groups["quoted"].Value.Replace("\"\"", "\"")
                : match.Groups["plain"].Value);
        }

        columns = found;
        return true;
    }

    private static string StripParentheses(string text)
    {
        text = text.Trim();
        while (IsWrapped(text))
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    /// <summary>True when the first character is a '(' closed by the last character, as in <c>(a AND b)</c> but not <c>(a) AND (b)</c>.</summary>
    private static bool IsWrapped(string text)
    {
        if (text.Length < 2 || text[0] != '(' || text[^1] != ')')
        {
            return false;
        }

        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0 && i < text.Length - 1)
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"\s+AND\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AndSeparator();

    [GeneratedRegex(@"^(?:\[(?<bracketed>(?:[^\]]|\]\])+)\]|""(?<quoted>(?:[^""]|"""")+)""|(?<plain>\w+))\s+IS\s+NOT\s+NULL$", RegexOptions.IgnoreCase)]
    private static partial Regex IsNotNullTerm();
}
