using MigrationTester.Planning;
using Xunit;

namespace MigrationTester.Tests;

public sealed class NotNullFilterTests
{
    [Theory]
    [InlineData("[Email] IS NOT NULL", "Email")]
    [InlineData("([Email] IS NOT NULL)", "Email")]
    [InlineData("[A] IS NOT NULL AND [B] IS NOT NULL", "A,B")]
    [InlineData("([A] IS NOT NULL AND [B] IS NOT NULL)", "A,B")]
    [InlineData("([A] IS NOT NULL) AND ([B] IS NOT NULL)", "A,B")]
    [InlineData("Email is not null", "Email")]
    [InlineData("[Odd]]Name] IS NOT NULL", "Odd]Name")]
    // As PostgreSQL stores and EF writes it.
    [InlineData("(\"Email\" IS NOT NULL)", "Email")]
    [InlineData("\"A\" IS NOT NULL AND \"Odd\"\"Name\" IS NOT NULL", "A,Odd\"Name")]
    public void TryParse_accepts_is_not_null_terms(string filter, string expected)
    {
        Assert.True(NotNullFilter.TryParse(filter, out var columns));
        Assert.Equal(expected.Split(','), columns);
    }

    [Theory]
    [InlineData("[Status] = 1")]
    [InlineData("[A] IS NOT NULL OR [B] IS NOT NULL")]
    [InlineData("[A] IS NULL")]
    [InlineData("[A] IS NOT NULL AND [Deleted] = 0")]
    [InlineData("")]
    public void TryParse_rejects_other_filters(string filter)
    {
        Assert.False(NotNullFilter.TryParse(filter, out _));
    }
}
