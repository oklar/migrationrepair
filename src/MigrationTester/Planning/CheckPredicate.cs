using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using MigrationTester.Schema;

namespace MigrationTester.Planning;

internal enum CheckOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    In,
    IsNotNull
}

/// <summary>A number or a string literal; exactly one of the two is set.</summary>
internal readonly record struct CheckLiteral(decimal? Number, string? Text);

/// <summary>One comparison of the column: with one literal, a list of them (IN), or none (IS NOT NULL).</summary>
internal sealed record CheckTerm(CheckOperator Operator, IReadOnlyList<CheckLiteral> Literals);

/// <summary>
/// Recognizes CHECK constraints that compare one column with literals, as most EF Core <c>HasCheckConstraint</c>
/// predicates do: <c>[Price] &gt; 0</c>, <c>[Status] IN (N'Open', N'Closed')</c>, <c>[Qty] &gt;= 1 AND [Qty] &lt;= 100</c>
/// or <c>"Email" IS NOT NULL</c>, also in the parenthesized form SQL Server stores, such as <c>([Price]&gt;(0))</c>.
/// Anything else (OR, NOT, functions, a second column, LIKE, BETWEEN, NULL literals, comments) is not recognized.
/// </summary>
internal sealed partial record CheckPredicate(string Column, IReadOnlyList<CheckTerm> Terms)
{
    // Keeps literal ± 1 inside the range of decimal.
    private const decimal MaxLiteral = 10_000_000_000_000_000_000_000_000_000m;

    // Values for = and IN on strings, tried in order until one matches no literal.
    private const string Fillers = "xyzabcdefghijklmnopqrstuvw";

    private static readonly string[] Keywords = ["AND", "IN", "IS", "NOT", "NULL", "OR"];

    public static bool TryParse(string? sql, [NotNullWhen(true)] out CheckPredicate? predicate)
    {
        predicate = null;
        if (string.IsNullOrWhiteSpace(sql) || Tokenize(sql) is not { } tokens)
        {
            return false;
        }

        var parser = new Parser(tokens);
        var terms = new List<(string Column, CheckTerm Term)>();
        if (!parser.Conjunction(terms) || !parser.AtEnd)
        {
            return false;
        }

        string column = terms[0].Column;
        if (terms.Any(t => !string.Equals(t.Column, column, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        predicate = new CheckPredicate(column, [.. terms.Select(static t => t.Term)]);
        return true;
    }

    /// <summary>
    /// A SQL literal that <paramref name="column"/> accepts but that makes one of the terms, and so the whole
    /// predicate, false; null when no such value is known for the column's type.
    /// </summary>
    public string? ViolatingValue(ColumnMetadata column)
    {
        foreach (var term in Terms)
        {
            // NULL only breaks IS NOT NULL: a comparison with NULL is unknown, which a CHECK constraint lets through.
            string? value = term.Operator == CheckOperator.IsNotNull
                ? (column.IsNullable ? "NULL" : null)
                : NumberViolating(term, column.Type) ?? TextViolating(term, column.Type);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string? NumberViolating(CheckTerm term, SqlType type)
    {
        if (type.Dialect.NumericRange(type) is not { } range || term.Literals.Any(static l => l.Number is null))
        {
            return null;
        }

        var (min, max, scale) = range;
        var numbers = term.Literals.Select(static l => l.Number!.Value).ToList();
        decimal[] candidates = term.Operator switch
        {
            CheckOperator.GreaterThan => [AtMost(numbers[0])],
            CheckOperator.GreaterThanOrEqual => [Below(numbers[0])],
            CheckOperator.LessThan => [AtLeast(numbers[0])],
            CheckOperator.LessThanOrEqual => [Above(numbers[0])],
            CheckOperator.Equal or CheckOperator.In => [Above(numbers.Max()), Below(numbers.Min())],
            // Only the literal itself breaks <>, and only if the column can hold it exactly.
            CheckOperator.NotEqual => AtMost(numbers[0]) == numbers[0] ? [numbers[0]] : [],
            _ => []
        };

        return candidates
            .Where(v => v >= min && v <= max)
            .Select(static v => v.ToString(CultureInfo.InvariantCulture))
            .FirstOrDefault();

        // The nearest values the column can hold on each side; float and real hold any literal, so step by 1.
        decimal AtMost(decimal x) => scale is int s ? Math.Round(x, s, MidpointRounding.ToNegativeInfinity) : x;
        decimal AtLeast(decimal x) => scale is int s ? Math.Round(x, s, MidpointRounding.ToPositiveInfinity) : x;
        decimal Below(decimal x) => AtMost(x) < x ? AtMost(x) : x - Step(scale);
        decimal Above(decimal x) => AtLeast(x) > x ? AtLeast(x) : x + Step(scale);
    }

    private static string? TextViolating(CheckTerm term, SqlType type)
    {
        if (!type.IsString || !type.IsComparable || term.Literals.Any(static l => l.Text is null))
        {
            return null;
        }

        var texts = term.Literals.Select(static l => l.Text!).ToList();
        IEnumerable<string> candidates = term.Operator switch
        {
            // A string is never different from, below or above itself, whatever the collation.
            CheckOperator.NotEqual or CheckOperator.LessThan or CheckOperator.GreaterThan => [texts[0]],
            CheckOperator.Equal or CheckOperator.In =>
                Fillers.Select(static c => c.ToString()).Where(f => !texts.Any(t => MayEqual(t, f))),
            // What sorts below or above a string depends on the collation.
            _ => []
        };

        return candidates.Where(c => Fits(c, type)).Select(type.Dialect.String).FirstOrDefault();
    }

    /// <summary>Equal under some collation: case, accents, width and trailing spaces ignored.</summary>
    private static bool MayEqual(string a, string b)
        => string.Compare(
            a.TrimEnd(' '),
            b.TrimEnd(' '),
            CultureInfo.InvariantCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth) == 0;

    /// <summary>Fits the column's length, and for char and varchar is plain ASCII, so no code page changes it.</summary>
    private static bool Fits(string text, SqlType type)
        => (type.Length is not int max || max == SqlType.Max || text.Length <= max)
            && (type.IsUnicodeString || text.All(char.IsAscii));

    private static decimal Step(int? scale) => scale is int s ? 1m / Pow10(s) : 1m;

    private static decimal Pow10(int exponent)
    {
        decimal result = 1m;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    private enum TokenKind { Name, Word, Number, Text, Symbol }

    private readonly record struct Token(TokenKind Kind, string Value);

    private static List<Token>? Tokenize(string sql)
    {
        // A comment could hide the rest of the predicate.
        if (sql.Contains("--", StringComparison.Ordinal) || sql.Contains("/*", StringComparison.Ordinal))
        {
            return null;
        }

        var tokens = new List<Token>();
        int pos = 0;
        while (true)
        {
            while (pos < sql.Length && char.IsWhiteSpace(sql[pos]))
            {
                pos++;
            }

            if (pos == sql.Length)
            {
                return tokens;
            }

            var match = TokenPattern().Match(sql, pos);
            if (!match.Success)
            {
                return null;
            }

            tokens.Add(match switch
            {
                _ when match.Groups["bracketed"].Success => new(TokenKind.Name, match.Groups["bracketed"].Value.Replace("]]", "]")),
                _ when match.Groups["quoted"].Success => new(TokenKind.Name, match.Groups["quoted"].Value.Replace("\"\"", "\"")),
                _ when match.Groups["text"].Success => new(TokenKind.Text, match.Groups["text"].Value.Replace("''", "'")),
                _ when match.Groups["number"].Success => new(TokenKind.Number, match.Value),
                _ when match.Groups["word"].Success => new(TokenKind.Word, match.Value),
                _ => new(TokenKind.Symbol, match.Value)
            });
            pos += match.Length;
        }
    }

    [GeneratedRegex("""
        \G(?:
            \[(?<bracketed>(?:[^\]]|\]\])+)\]
          | "(?<quoted>(?:[^"]|"")+)"
          | [Nn]?'(?<text>(?:[^']|'')*)'
          | (?<number>\d+(?:\.\d*)?|\.\d+)
          | (?<word>[A-Za-z_][A-Za-z0-9_@$\#]*)
          | <=|>=|<>|!=|[=<>(),+-]
        )
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Recursive descent over <c>term (AND term)*</c>, where a term is a parenthesized group of terms,
    /// <c>column op literal</c>, <c>literal op column</c>, <c>column IN (literal, ...)</c> or <c>column IS NOT NULL</c>.
    /// Columns and literals may be parenthesized.
    /// </summary>
    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        public bool AtEnd => _pos == tokens.Count;

        public bool Conjunction(List<(string Column, CheckTerm Term)> terms)
        {
            do
            {
                if (!Term(terms))
                {
                    return false;
                }
            }
            while (AcceptWord("AND"));

            return true;
        }

        private bool Term(List<(string Column, CheckTerm Term)> terms)
        {
            int start = _pos;
            int count = terms.Count;
            if (AcceptSymbol("(") && Conjunction(terms) && AcceptSymbol(")"))
            {
                return true;
            }

            // Not a group, e.g. ([Price]) > 0 or (0) < [Price].
            _pos = start;
            terms.RemoveRange(count, terms.Count - count);

            if (Column() is { } column)
            {
                if (AcceptWord("IS"))
                {
                    return AcceptWord("NOT") && AcceptWord("NULL") && Add(terms, column, CheckOperator.IsNotNull, []);
                }

                if (AcceptWord("IN"))
                {
                    return LiteralList() is { } list && Add(terms, column, CheckOperator.In, list);
                }

                return Comparison() is { } op && Literal() is { } literal && Add(terms, column, op, [literal]);
            }

            return Literal() is { } left
                && Comparison() is { } mirrored
                && Column() is { } right
                && Add(terms, right, Mirror(mirrored), [left]);
        }

        private static bool Add(
            List<(string Column, CheckTerm Term)> terms,
            string column,
            CheckOperator op,
            IReadOnlyList<CheckLiteral> literals)
        {
            terms.Add((column, new CheckTerm(op, literals)));
            return true;
        }

        private static CheckOperator Mirror(CheckOperator op) => op switch
        {
            CheckOperator.LessThan => CheckOperator.GreaterThan,
            CheckOperator.LessThanOrEqual => CheckOperator.GreaterThanOrEqual,
            CheckOperator.GreaterThan => CheckOperator.LessThan,
            CheckOperator.GreaterThanOrEqual => CheckOperator.LessThanOrEqual,
            _ => op
        };

        private string? Column()
        {
            int start = _pos;
            if (AcceptSymbol("("))
            {
                if (Column() is { } inner && AcceptSymbol(")"))
                {
                    return inner;
                }

                _pos = start;
                return null;
            }

            if (Peek() is { Kind: TokenKind.Name } || (Peek() is { Kind: TokenKind.Word } word && !IsKeyword(word.Value)))
            {
                return tokens[_pos++].Value;
            }

            return null;
        }

        /// <summary><c>(literal, ...)</c> of all numbers or all strings.</summary>
        private List<CheckLiteral>? LiteralList()
        {
            if (!AcceptSymbol("("))
            {
                return null;
            }

            var list = new List<CheckLiteral>();
            do
            {
                if (Literal() is not { } item)
                {
                    return null;
                }

                list.Add(item);
            }
            while (AcceptSymbol(","));

            bool sameKind = list.All(static l => l.Number is not null) || list.All(static l => l.Text is not null);
            return AcceptSymbol(")") && sameKind ? list : null;
        }

        private CheckLiteral? Literal()
        {
            int start = _pos;
            if (AcceptSymbol("("))
            {
                if (Literal() is { } inner && AcceptSymbol(")"))
                {
                    return inner;
                }
            }
            else if (AcceptSymbol("-") || AcceptSymbol("+"))
            {
                bool negate = tokens[start].Value == "-";
                if (Literal() is { Number: decimal n })
                {
                    return new CheckLiteral(negate ? -n : n, null);
                }
            }
            else if (Peek() is { Kind: TokenKind.Number } number
                && decimal.TryParse(number.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value)
                && value <= MaxLiteral)
            {
                _pos++;
                return new CheckLiteral(value, null);
            }
            else if (Peek() is { Kind: TokenKind.Text } text)
            {
                _pos++;
                return new CheckLiteral(null, text.Value);
            }

            _pos = start;
            return null;
        }

        private CheckOperator? Comparison()
        {
            CheckOperator? op = Peek() is { Kind: TokenKind.Symbol } symbol
                ? symbol.Value switch
                {
                    "=" => CheckOperator.Equal,
                    "<>" or "!=" => CheckOperator.NotEqual,
                    "<" => CheckOperator.LessThan,
                    "<=" => CheckOperator.LessThanOrEqual,
                    ">" => CheckOperator.GreaterThan,
                    ">=" => CheckOperator.GreaterThanOrEqual,
                    _ => null
                }
                : null;
            if (op is not null)
            {
                _pos++;
            }

            return op;
        }

        private Token? Peek() => _pos < tokens.Count ? tokens[_pos] : null;

        private bool AcceptSymbol(string symbol) => Accept(TokenKind.Symbol, symbol);

        private bool AcceptWord(string keyword) => Accept(TokenKind.Word, keyword);

        private bool Accept(TokenKind kind, string value)
        {
            if (Peek() is { } token && token.Kind == kind && string.Equals(token.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                _pos++;
                return true;
            }

            return false;
        }

        private static bool IsKeyword(string word) => Keywords.Contains(word, StringComparer.OrdinalIgnoreCase);
    }
}
