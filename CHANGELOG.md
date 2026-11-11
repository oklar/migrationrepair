# Changelog

All notable changes to MigrationTester. Versions follow [Semantic Versioning](https://semver.org); before 1.0 any release may break the API.

## [Unreleased]

### Added

- Column type changes that SQL Server converts without an error but that change values now fail the migration, as `ScenarioKind.ColumnTypeConversionLossy`. One row gets a value the change alters, such as an emoji in a column going from `nvarchar` to `varchar` (it becomes `??`), `0.0001` when a `decimal` scale shrinks to 2, a time of day in a column becoming `date`, an offset in a `datetimeoffset` becoming `datetime2`, `'007'` in text becoming `int`, or `1234567.891` in a `float` becoming text. Right before the `ALTER COLUMN` MigrationTester reads the column's values, and right after it fails the step if one does not come back when converted to the old type. The step's summary shows each value and what it became.
- The repair hint for such a change is the statement that makes the loss explicit, to put before the `AlterColumn`: `UPDATE … SET [c] = CAST(CAST([c] AS <new type>) AS <old type>)`, which stores in the old column what the new type keeps, so the type change itself changes nothing. It also says how to keep the values instead (a Unicode type or `_UTF8` collation, `SWITCHOFFSET` to UTC for offsets, a copy with a lossless `CONVERT` style for numbers and dates becoming text).
- The sample project's migration 10 changes a customer name from `nvarchar` to `varchar` and fails on an emoji.

### Changed

- Migrations whose type change alters values now fail; before, they passed. Type changes that keep every value (widening, `varchar` to `nvarchar`, a UTF-8 collation, `decimal` or `datetime2` to text) or only reformat one (text to `uniqueidentifier`, `nvarchar` to `nchar`) are not reported, and neither are drops: a column dropped later in the same `Up()` is `NotApplicable`. A step that fails this way has `Outcome = Failed` with a null `Exception`, and the changed scenarios in `FailingScenarios`.
- Migrations that run outside a transaction, the last step under `KeepDatabase`, and every step under `RebuildDatabaseForEachMigration` now run the migration's script the way the default mode does, in a transaction committed when it succeeds, instead of calling `Migrate`, so every mode checks the same values.
- Type names in scenario names and reasons include a `datetime2`, `datetimeoffset` or `time` column's fractional-second digits, e.g. `datetime2(7)`.
- Dependencies: EF Core 10.0.14 for the tests and sample, `System.Security.Cryptography.Pkcs` 10.0.14.

## [0.1.0] - 2026-10-27

First stable release: the same code and API as 0.1.0-preview.5, now recorded in `PublicAPI.Shipped.txt`.

### Changed

- The package names Larsenq as author and copyright holder (`Copyright (c) 2026 Larsenq`), and the repository has the MIT `LICENSE` text that the package's license expression already declared.

## [0.1.0-preview.5] - 2026-10-27

The API as planned for 0.1.0.

### Changed

- `MigrationScenario`, `MigrationStepResult` and `MigrationTestReport` can no longer be created or changed outside the library: their constructors and property setters are internal, and their properties stay readable. They are results, and this keeps them free to change after 0.1.0 without breaking callers; it also rules out a hand-made `MigrationStepResult` whose `Succeeded` disagrees with `RequireAllScenariosSeeded`.
- Doc fixes: `RunAsync` says it ignores `MigrationIds` and `AfterMigrationId`, `MigrationStepResult.Duration` describes what the default mode does, and `MigrationScenario.Schema` says it holds the default schema when the operation names none.

## [0.1.0-preview.4] - 2026-10-27

### Changed

- A foreign key moved to another table while the old foreign key over the same columns still holds at N-1, as EF scaffolds when a navigation moves to another entity and its foreign key property is renamed, now gets planted data instead of being `NotSeeded` because the planted orphan conflicted with the old foreign key. One row holds a key of the old principal that the new principal lacks, taken from an old-principal row added for it. That row's key comes from a range no other seeded row uses, so it also differs from keys seeded in the new principal, which in two tables with GUID or identity keys follow the same sequence. When the old principal's key is itself a foreign key to the new principal key, as for a relationship moved from a derived type to its base type (table-per-type), the case is `NotApplicable`: every key the rows can hold is in the new principal.
- Duplicates for a new unique index, unique constraint or primary key are planted on tables whose existing unique key has only foreign key columns left to change, such as a join table with primary key `(StudentId, CourseId)` getting a unique index on `StudentId`, instead of being `NotSeeded`. The copied row references a principal row added for it; when that principal's key is a foreign key too, its own principal gets a row the same way.

## [0.1.0-preview.3] - 2026-10-27

### Added

- `MigrationTestOptions.AfterMigrationId` makes `RunAllAsync` test only the migrations after the one it names, so a project can start testing new migrations without its history, which may fail on data it has long since run past. With no migrations after it the report has no steps and succeeds. `RunRangeAsync` rejects it, as it does `MigrationIds`.
- Tests that a context with `EnableRetryOnFailure` reports exactly what one without it reports, over every schema case.

### Changed

- Operations on a column added earlier in the same `Up()` are tested on what the existing rows get in it, instead of being reported as `NotSeeded` because the column does not exist at N-1:
  - a foreign key, CHECK constraint or type change on a NOT NULL column with a default is tested against that default in the existing rows, so a new required relationship that EF scaffolds with a `Guid.Empty` or `0` default now fails when the existing rows are not filled in first;
  - a nullable column made NOT NULL later in the migration is tested against the NULL in the existing rows, so it fails unless the migration fills the column in between;
  - foreign keys, CHECK constraints, `IS NOT NULL` filtered indexes and type changes on a nullable new column are `NotApplicable`, since the existing rows hold NULL in it, even when it has a default;
  - a unique index that includes a new column is planted by copying a row, as the copy gets the same NULL or default in it.

  The scenario's `Name` says which columns the migration adds.
- A foreign key dropped and added again over the same columns and principal key, as EF scaffolds for a change of delete behavior, is `NotApplicable` instead of `NotSeeded`: the existing rows already satisfy it.
- A report with no steps reads "MigrationTester: no migrations to test."

## [0.1.0-preview.2] - 2026-10-25

### Added

- New CHECK constraints get planted data when they compare one column with literals: `=`, `<>`, `!=`, `<`, `<=`, `>`, `>=`, `IN (...)` and `IS NOT NULL`, joined by `AND`, as in `[Price] > 0`, `[Status] IN (N'Open', N'Closed')` or `[Quantity] >= 1 AND [Quantity] <= 100`. One row gets a value that the column accepts before the migration but the constraint rejects, such as `0`, `N'x'` or a NULL. Other predicates, such as `LEN([Code]) = 3` or `[Start] < [End]`, are still `NotSeeded`.
- The repair hint for a CHECK constraint includes a query for the rows that fail its predicate.

### Changed

- Migrations that add such a CHECK constraint without handling existing rows that break it now fail; before, they passed untested. The sample's migration 09 (`[Price] > 0`) now fails on purpose.
- `FailingScenarios` matches a FOREIGN KEY or CHECK conflict (SQL Server error 547) only to the scenario planted for the constraint the error names.

### Fixed

- Operations that use a name given by a `RenameColumn` or `RenameTable` earlier in the same `Up()` now get planted data, instead of being reported as `NotSeeded` because the new name does not exist at N-1. EF scaffolds this for a renamed column that becomes NOT NULL or gets a unique index, for example. Renames are followed in order, through chains and moves to another schema; the scenario's `Table` and `Columns` hold the N-1 names, its `Name` mentions the rename, and `FailingScenarios` matches SQL Server errors that use the new names.

## [0.1.0-preview.1] - 2026-10-24

First preview release.

### Added

- `MigrationTestRunner.RunAllAsync`, `RunRangeAsync`, `RunAsync` and `GetMigrationIds` for testing EF Core migrations against SQL Server, as one run or one test case per migration.
- Planted data for every migration, valid before it runs:
  - duplicates for new unique indexes, unique constraints and primary keys, including `IS NOT NULL` filtered indexes;
  - orphan rows for new foreign keys;
  - NULLs for columns becoming NOT NULL;
  - over-long values for shrinking columns;
  - unconvertible values for type changes, including numeric narrowing, decimal precision or scale changes and `datetime2` → `datetime`;
  - existing rows for NOT NULL columns added without a default.
- Works across schemas and with identity, rowversion, computed, temporal and GUID-keyed tables; seeded keys avoid the ids HasData rows use.
- `MigrationTestReport` and `MigrationStepResult`:
  - per-step `Outcome` (`Passed`, `Failed`, `Error`);
  - per-scenario `Status` (`Planted`, `NotApplicable`, `NotSeeded`) with a reason;
  - `FailingScenarios` matched from SQL Server error numbers;
  - T-SQL repair hints;
  - `ThrowIfFailed()`, which throws with a readable report.
- A throwaway test database per run, dropped afterwards; `DatabaseName` and `KeepDatabase` to opt out.
- Each migration runs on its planted data in a transaction that is rolled back, so a run costs about two migration runs per migration; `RebuildDatabaseForEachMigration` recreates the database for every migration instead.
- `RequireAllScenariosSeeded` to fail migrations whose scenarios could not all be planted.

[Unreleased]: https://github.com/oklar/migrationtester/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/oklar/migrationtester/compare/v0.1.0-preview.5...v0.1.0
[0.1.0-preview.5]: https://github.com/oklar/migrationtester/compare/v0.1.0-preview.4...v0.1.0-preview.5
[0.1.0-preview.4]: https://github.com/oklar/migrationtester/compare/v0.1.0-preview.3...v0.1.0-preview.4
[0.1.0-preview.3]: https://github.com/oklar/migrationtester/compare/v0.1.0-preview.2...v0.1.0-preview.3
[0.1.0-preview.2]: https://github.com/oklar/migrationtester/compare/v0.1.0-preview.1...v0.1.0-preview.2
[0.1.0-preview.1]: https://github.com/oklar/migrationtester/releases/tag/v0.1.0-preview.1
