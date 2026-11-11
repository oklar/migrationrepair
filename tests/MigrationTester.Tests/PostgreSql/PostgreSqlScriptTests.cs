using MigrationTester.Providers.PostgreSql;
using Xunit;

namespace MigrationTester.Tests.PostgreSql;

/// <summary><see cref="PostgreSqlScript.Statements"/> splits only at the semicolons that end a statement.</summary>
public sealed class PostgreSqlScriptTests
{
    [Fact]
    public void Splits_at_each_statement()
    {
        var statements = PostgreSqlScript.Statements("""
            ALTER TABLE "Items" ALTER COLUMN "Note" TYPE text;
            ALTER TABLE "Items" ALTER COLUMN "Note" SET NOT NULL;

            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260101000002_Note', '10.0.3');
            """);

        Assert.Equal(
            [
                "ALTER TABLE \"Items\" ALTER COLUMN \"Note\" TYPE text;",
                "ALTER TABLE \"Items\" ALTER COLUMN \"Note\" SET NOT NULL;",
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\")\nVALUES ('20260101000002_Note', '10.0.3');"
            ],
            statements.Select(static s => s.ReplaceLineEndings("\n")));
    }

    [Fact]
    public void Keeps_a_dollar_quoted_body_whole()
    {
        const string block = """
            DO $EF$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'mood') THEN
                    CREATE TYPE mood AS ENUM ('sad', 'happy');
                END IF;

            END $EF$;
            """;

        var statements = PostgreSqlScript.Statements(block + "\nSELECT 1;");

        Assert.Equal([block.Trim(), "SELECT 1;"], statements);
    }

    [Theory]
    [InlineData("SELECT 'a;b';")]
    [InlineData("SELECT 'it''s; fine';")]
    [InlineData("SELECT E'it\\'s; fine';")]
    [InlineData("SELECT \"odd;name\" FROM t;")]
    [InlineData("SELECT $$a;b$$;")]
    [InlineData("SELECT 1 -- a; comment\n;")]
    [InlineData("SELECT /* a; /* nested; */ comment */ 1;")]
    public void Ignores_semicolons_inside_literals_identifiers_and_comments(string sql)
    {
        Assert.Equal([sql], PostgreSqlScript.Statements(sql));
    }

    [Fact]
    public void Keeps_comments_with_the_next_statement_and_leaves_out_trailing_ones()
    {
        Assert.Equal(["-- header\nSELECT 1;"], PostgreSqlScript.Statements("-- header\nSELECT 1;\n/* trailing */\n"));
    }

    [Fact]
    public void Keeps_a_last_statement_without_a_semicolon()
    {
        Assert.Equal(["SELECT 1;", "SELECT $1"], PostgreSqlScript.Statements("SELECT 1;\nSELECT $1"));
    }
}
