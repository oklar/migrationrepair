# MigrationTester

[![CI](https://github.com/oklar/migrationtester/actions/workflows/ci.yml/badge.svg)](https://github.com/oklar/migrationtester/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/MigrationTester)](https://www.nuget.org/packages/MigrationTester)

Tests EF Core migrations against SQL Server with the kind of data production already has. For each migration N, MigrationTester brings a test database to N−1, plants rows that are valid there but that N's `Up()` may not handle, then runs N. When the migration fails you get the SQL Server error, the planted data it broke on, and a T-SQL repair hint. A migration also fails when one of its column type changes silently changes a planted value, such as an emoji that `varchar` keeps only as `??`.

**Target:** EF Core 10 / .NET 10, SQL Server. Your test project references `Microsoft.EntityFrameworkCore.SqlServer`, as your app does.

```bash
dotnet add package MigrationTester
```

## What it plants

| When a migration… | it plants |
|---|---|
| adds a unique index, unique constraint or primary key | two rows with the same key (including EF's `IS NOT NULL` filtered indexes) |
| adds a foreign key | a row pointing at a principal that does not exist; for a foreign key moved from another table, a key of that table which the new principal lacks |
| makes a column NOT NULL | a NULL |
| shrinks a string or binary column | a value one longer than the new maximum |
| changes a column's type | a value the new type cannot hold: text in a column becoming a number, GUID or date; `32768` in one becoming `smallint`; `10^14` in a `decimal(18,2)` becoming `decimal(18,4)`; a year-1000 date in one becoming `datetime` |
| adds a NOT NULL column without a default | an existing row |
| adds a CHECK constraint that compares one column with literals (`=`, `<>`, `<`, `<=`, `>`, `>=`, `IN (...)`, `IS NOT NULL`, joined by `AND`) | a value that breaks it: `0` for `[Price] > 0`, `N'x'` for `[Status] IN (N'Open', N'Closed')`, a NULL for `IS NOT NULL` |
| changes a column's type in a way SQL Server converts without an error but changes some values | a value it changes: an emoji in a column going from `nvarchar` to `varchar`; `0.0001` when a `decimal` scale shrinks to 2; a time of day in one becoming `date`; `+02:00` in a `datetimeoffset` becoming `datetime2`; `'007'` in text becoming `int`; `1234567.891` in a `float` becoming text (`'1.23457e+006'`) |

### Data that changes without an error

A type change must not change values by itself. Right before each such `ALTER COLUMN`, MigrationTester reads the column's values; right after it, it converts the column back to its old type and fails the step if a value does not come back. Text is compared exactly (accents and case count, trailing spaces do not), numbers and dates by value, so `1.0000` becoming `'1.0000'` is fine and `'007'` becoming `7` is not.

If the loss is intended, the migration says so by changing the values itself before the type change; the repair hint gives the statement, which stores in the old column what the new type keeps:

```csharp
migrationBuilder.Sql("UPDATE [Customers] SET [Name] = CAST(CAST([Name] AS varchar(256)) AS nvarchar(256)) WHERE [Name] IS NOT NULL;");
migrationBuilder.AlterColumn<string>(name: "Name", table: "Customers", type: "varchar(256)", unicode: false, ...);
```

If the values matter, keep a type that holds them instead (here `nvarchar`, or a `_UTF8` collation). Type changes that keep every value (widening, `varchar` to `nvarchar`, numbers or `datetime2` to text) or only reformat one (text to `uniqueidentifier`) are never reported. Dropping a column or table is not checked: that loss is on purpose.

Every planted row is valid before the migration: foreign keys point at real rows, existing unique keys stay unique, values fit the old column. So a failure points at the migration, not at the test data. The data works across schemas and with identity, rowversion, computed, temporal and GUID-keyed tables.

## Quick start

```csharp
var options = new MigrationTestOptions
{
    CreateContext = () => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=localhost;Database=MyApp;Integrated Security=True;TrustServerCertificate=True")
        .Options)
};

var report = await MigrationTestRunner.RunAllAsync(options);
report.ThrowIfFailed();
```

`ThrowIfFailed()` throws a `MigrationTestException` whose message is the full report, so any test framework shows it:

```text
FAILED 20240301090000_AddOrderStatus: Up() failed on data that is valid before it runs.
SQL Server error 4901: ALTER TABLE only allows columns to be added that can contain nulls, or have a DEFAULT definition specified, ...

Broke on:
 - [NotNullColumnAddedWithoutDefault] NOT NULL column 'Status' added to 'Orders' without a default value

Repair hint:
-- Column [Status] is added as NOT NULL without a default value.
-- Either add a DEFAULT in the migration, or add it as nullable, UPDATE it, then make it NOT NULL.
```

### One test per migration

```csharp
public static TheoryData<string> Migrations => new(MigrationTestRunner.GetMigrationIds(Options.CreateContext));

[Theory]
[MemberData(nameof(Migrations))]
public async Task Migration_handles_existing_data(string migrationId)
{
    var step = await MigrationTestRunner.RunAsync(Options, migrationId);
    step.ThrowIfFailed();
}
```

## The test database

By default the runner never touches the database in your connection string. Each run creates a throwaway database named after it (`MyApp_mt_1a2b3c4d`) and drops it when the run ends. The connection string only needs a server and a login that can create and drop databases.

A run migrates that database forward once. For each migration it plants the data and runs the migration inside a transaction that is rolled back, then applies the migration to the clean database for the next step. That is about two migration runs per migration, with the same starting state as rebuilding from empty every time; on the sample's 9 migrations it takes 0.65 s instead of 5 s. Migrations with statements that cannot run in a transaction (`Sql(..., suppressTransaction: true)`, `ALTER DATABASE`) are planted and run for real, and the database is rebuilt afterwards.

| Option | Effect |
|---|---|
| `DatabaseName` | Use this exact database instead of a throwaway one, e.g. on a server where creating databases is slow. It is dropped and recreated, so only name a database you are happy to lose. |
| `KeepDatabase` | Leave the database in place after the run, holding the last migration's planted data and result; `report.DatabaseName` tells you which one. |
| `RebuildDatabaseForEachMigration` | Recreate the database from empty and plant data for real for every migration. Slower; for when you suspect the transactional run behaves differently. |

A context configured with `EnableRetryOnFailure` works as it is; there is no need to turn the retrying execution strategy off for the tests.

## Reading the results

`RunAllAsync` and `RunRangeAsync` return a `MigrationTestReport`; `RunAsync` returns one `MigrationStepResult`.

| Member | Meaning |
|---|---|
| `Outcome` | `Passed`, `Failed` (the migration threw on the planted data, or a type change in it changed a planted value; `Exception` is then null) or `Error` (the step could not be set up, e.g. an earlier migration fails on an empty database) |
| `Scenarios` | Every case derived from `Up()`, each with a `Status`: `Planted`, `NotApplicable` (it cannot fail, e.g. the table is new) or `NotSeeded` (it could fail but could not be planted; see `Reason`) |
| `FailingScenarios` | The planted cases the SQL Server error matches, and those a type change changed (`ColumnTypeConversionLossy`; the summary shows each value and what it became) |
| `Summary` / `ToString()` | The readable text shown above |
| `Warnings` | E.g. `migrationBuilder.Sql(...)` present, or a table that got no row |

Set `RequireAllScenariosSeeded = true` to also fail migrations with `NotSeeded` cases, so nothing passes untested.

## Choosing migrations

| Call | Tests |
|---|---|
| `RunAllAsync(options)` | every migration, those in `options.MigrationIds`, or those after `options.AfterMigrationId`, in EF apply order |
| `RunRangeAsync(options, from, to)` | an inclusive slice of apply order, e.g. to split a long history across CI jobs |
| `RunAsync(options, id)` | one migration; `id` can be the full id or the migration name |

On a project with a history, older migrations may fail on data they have long since run past. Set `AfterMigrationId` to the last migration already in production, and `RunAllAsync` tests only the ones after it; with none after it yet, the report has no steps and succeeds:

```csharp
var report = await MigrationTestRunner.RunAllAsync(new MigrationTestOptions
{
    CreateContext = options.CreateContext,
    AfterMigrationId = "20250601120000_AddInvoices"
});
report.ThrowIfFailed();
```

## SQL Server in Docker

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Your_password1" -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest
```

Then use `Server=127.0.0.1,1433;User Id=sa;Password=Your_password1;TrustServerCertificate=True` as the connection string.

## Limitations

- **SQL Server only.** Other EF Core providers need their own catalog queries, seed SQL and error handling.
- **Only `Up()` is tested**, and only operations built with `MigrationBuilder`; what `migrationBuilder.Sql(...)` does is not analyzed (flagged with a warning and `ContainsRawSql`).
- **Deterministic cases, not random data.** A migration can still fail on data nobody thought of.
- **Seeding** inserts one ordinary row per table and derives the bad rows from it. When SQL Server rejects a seed statement (an existing CHECK constraint, a trigger, a column type with no generator, a required self-referencing foreign key), those cases become `NotSeeded` with the reason, and the run carries on. Filtered unique indexes are seeded only when the filter is `IS NOT NULL` terms.
- **New CHECK constraints** are seeded only when they compare one column with literals, as in the table above, and the column is a number (`int`, `decimal`, `money`, `float`, `bit` and the like) or a `char`/`varchar`/`nchar`/`nvarchar` string; for strings only `=`, `<>`, `<`, `>` and `IN` are used, since what sorts below or above a value depends on the collation. Anything else, such as `LEN([Code]) = 3`, `[Start] < [End]`, `OR`, or a date column, is reported as `NotSeeded`.
- **Renames are followed within one `Up()`.** Operations after a `RenameColumn` or `RenameTable` (including a move to another schema) are planted under the N-1 names, which the scenario's `Table` and `Columns` hold. A rename done another way, such as `sp_rename` in `migrationBuilder.Sql(...)`, is not followed.
- **Columns added earlier in the same `Up()`** cannot hold planted data, since they do not exist at N−1; the existing rows and what they get in the column are the data. A NOT NULL column with a default gets that default in every row, so a foreign key on it (EF scaffolds `Guid.Empty` or `0` for a new required relationship) is tested against it. A nullable column holds NULL in every row, even with a default, so a column added as nullable and then made NOT NULL fails unless the migration fills it in between, while foreign keys, CHECK constraints and `IS NOT NULL` filtered indexes on it are `NotApplicable`.
- **Duplicates** copy a row, keep the new key's columns and give every existing unique key a new value. When an existing key has only foreign key columns left to change, as in a join table whose primary key is its two foreign keys, the copy references a principal row added for it. A foreign key over several columns, or to its own table, still makes such a case `NotSeeded`.
- **A foreign key dropped and added again** over the same columns and principal key (as EF scaffolds for a change of delete behavior) is `NotApplicable`: the existing rows already satisfy it.
- **A foreign key moved to another table**, while the old foreign key over the same columns still holds at N−1, is planted with a key of the old principal that the new one lacks, from an old-principal row added for it. That row's key comes from a range no other seeded row uses (integer keys from 2,000,000, 32,000 for `smallint` and 200 for `tinyint`, GUIDs with `0001` as their fourth group, strings starting with `w`), so it also differs from rows added to the new principal, as when the two are one-to-one; a key of another type, such as a date or a one-character string, or one that is a foreign key to a third table, makes the case `NotSeeded`. When the old principal's key is itself a foreign key to the new principal key, as for a relationship moved from a derived type to its base type (table-per-type), it is `NotApplicable`: every key the rows can hold is in the new principal. An old foreign key that covers only some of the columns is not followed, so the planted value conflicts with it and the case is `NotSeeded`.
- **Data loss is checked for column type changes only**, and only with the value MigrationTester plants for the change: a type change it knows no such value for passes untested. Values a migration deletes on purpose (`DropColumn`, `DropTable`, `DeleteData`, a column dropped later in the same `Up()`) or changes in `migrationBuilder.Sql(...)` are not checked, nor is a rename done as a drop and an add, which EF scaffolds when it cannot tell a renamed property from a new one.
- **Seed values avoid HasData ids** by starting identity counters and integer keys at 1,000,000 (30,000 for `smallint`, 100 for `tinyint`). Seed data with ids above that can still collide.

## More

- [Changelog](https://github.com/oklar/migrationtester/blob/main/CHANGELOG.md)
- [Sample project](https://github.com/oklar/migrationtester/tree/main/examples/MigrationTester.Examples): ten migrations that each break on a different kind of data
- [Contributing](https://github.com/oklar/migrationtester/blob/main/CONTRIBUTING.md): building, testing and releasing
