namespace MigrationTester.Providers.PostgreSql;

/// <summary>
/// Splits a PostgreSQL script into its statements, as psql does: at the semicolons outside string literals, quoted
/// identifiers, dollar-quoted bodies and comments. Statements that hold only comments are left out.
/// </summary>
internal static class PostgreSqlScript
{
    public static List<string> Statements(string script)
    {
        var statements = new List<string>();
        int start = 0;
        bool hasCode = false;
        int i = 0;
        while (i < script.Length)
        {
            char c = script[i];
            if (c == '\'')
            {
                i = SkipQuoted(script, i, '\'', backslashEscapes: IsEscapeString(script, i));
                hasCode = true;
            }
            else if (c == '"')
            {
                i = SkipQuoted(script, i, '"', backslashEscapes: false);
                hasCode = true;
            }
            else if (c == '-' && At(script, i + 1) == '-')
            {
                int end = script.IndexOf('\n', i);
                i = end < 0 ? script.Length : end + 1;
            }
            else if (c == '/' && At(script, i + 1) == '*')
            {
                i = SkipBlockComment(script, i);
            }
            else if (c == '$' && DollarTag(script, i) is { } tag)
            {
                int end = script.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = end < 0 ? script.Length : end + tag.Length;
                hasCode = true;
            }
            else if (c == ';')
            {
                Add(i + 1);
                start = i + 1;
                hasCode = false;
                i++;
            }
            else
            {
                hasCode |= !char.IsWhiteSpace(c);
                i++;
            }
        }

        Add(script.Length);
        return statements;

        void Add(int end)
        {
            if (hasCode)
            {
                statements.Add(script[start..end].Trim());
            }
        }
    }

    private static char At(string text, int index) => index < text.Length ? text[index] : '\0';

    /// <summary>The index after the literal or identifier that starts at <paramref name="open"/>; doubled quotes stay inside it.</summary>
    private static int SkipQuoted(string text, int open, char quote, bool backslashEscapes)
    {
        int i = open + 1;
        while (i < text.Length)
        {
            if (backslashEscapes && text[i] == '\\')
            {
                i += 2;
            }
            else if (text[i] == quote)
            {
                if (At(text, i + 1) != quote)
                {
                    return i + 1;
                }

                i += 2;
            }
            else
            {
                i++;
            }
        }

        return text.Length;
    }

    /// <summary>An <c>E'...'</c> string, in which a backslash escapes the next character.</summary>
    private static bool IsEscapeString(string text, int quote)
        => quote > 0 && text[quote - 1] is 'E' or 'e' && (quote == 1 || !IsIdentifierChar(text[quote - 2]));

    /// <summary>The index after the block comment that starts at <paramref name="open"/>; PostgreSQL nests them.</summary>
    private static int SkipBlockComment(string text, int open)
    {
        int depth = 0;
        int i = open;
        while (i < text.Length)
        {
            if (text[i] == '/' && At(text, i + 1) == '*')
            {
                depth++;
                i += 2;
            }
            else if (text[i] == '*' && At(text, i + 1) == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return i;
                }
            }
            else
            {
                i++;
            }
        }

        return text.Length;
    }

    /// <summary>The <c>$tag$</c> or <c>$$</c> that opens a dollar-quoted body at <paramref name="dollar"/>, or null for none (e.g. <c>$1</c>).</summary>
    private static string? DollarTag(string text, int dollar)
    {
        if (dollar > 0 && IsIdentifierChar(text[dollar - 1]))
        {
            return null;
        }

        int i = dollar + 1;
        if (At(text, i) is var first && (char.IsLetter(first) || first == '_'))
        {
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            {
                i++;
            }
        }

        return At(text, i) == '$' ? text[dollar..(i + 1)] : null;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
