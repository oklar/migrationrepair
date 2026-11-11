# Contributing

## Layout

| Path | Contents |
|------|----------|
| `src/MigrationTester/` | The library (the only packable project) |
| `tests/MigrationTester.Tests/` | Unit, SQL Server and PostgreSQL integration tests (xUnit.net v3) |
| `examples/MigrationTester.Examples/` | Consumer sample; see its [README](examples/MigrationTester.Examples/README.md) |

Shared build settings live in `Directory.Build.props`, package versions in `Directory.Packages.props`. Warnings fail the build, including NuGet vulnerability warnings and missing XML docs on public members.

## Build and test

```bash
dotnet build MigrationTester.slnx
dotnet test MigrationTester.slnx
```

Integration tests need SQL Server. The defaults in `TestSqlServerConfig` use Windows auth against the default local instance; override them with `MIGRATION_TESTER_SQL_TEST` (tests) and `MIGRATION_TESTER_SQL_DESIGN` (`dotnet ef`). For a SQL Express named instance, for example:

```bash
export MIGRATION_TESTER_SQL_TEST='Server=localhost\SQLEXPRESS;Database=MigrationTester_Test;Integrated Security=True;TrustServerCertificate=True'
```

The examples use `MIGRATION_TESTER_SCENARIO_TEST` the same way. Tests run in throwaway databases named after the configured one (`MigrationTester_Test_mt_…`). Before the integration tests start, any left behind by an interrupted run are dropped.

The PostgreSQL integration tests (`tests/MigrationTester.Tests/PostgreSql/`) only need Docker running: Testcontainers starts a PostgreSQL 17 container before the first of them and removes it after the last, also when the run is interrupted. Without Docker they are skipped. To use a server of your own instead, as CI does with its service container, set `MIGRATION_TESTER_PG_TEST`:

```bash
export MIGRATION_TESTER_PG_TEST='Host=localhost;Port=5432;Database=MigrationTester_Test;Username=postgres;Password=...'
```

**Providers.** Everything a database does differently lives in `src/MigrationTester/Providers/`: a `DatabaseProvider` (catalog queries, connection strings, script batches, transaction checks, error codes, reading a column's values around its `ALTER COLUMN`) and a `SqlDialect` (identifiers, literals, types, seed statement shapes, failing and lossy conversions, NULL and default semantics, repair hint fragments) per database. The planner and seeder only talk to the dialect. SQL Server's script splits at `GO`; PostgreSQL's splits into statements (`PostgreSqlScript`), so the values can be read right around the `ALTER COLUMN ... TYPE` statement.

The PostgreSQL schema cases (`PostgreSql/PostgreSqlSchemaCaseTests.cs`) cover each scenario kind, join tables, a moved foreign key, the data-loss cases, a type change the migration makes explicit first, and the same report in both run modes.

**Schema cases.** `tests/MigrationTester.Tests/SchemaCases/` runs end to end over hand-written migrations for common schema shapes: rowversion, computed and temporal tables, GUID keys, non-`dbo` schemas, required foreign keys, filtered unique indexes, type narrowing, HasData ids, seed rows rejected by a CHECK constraint, statements outside a transaction, a broken earlier migration, renames, columns added and used in the same migration, a foreign key added again or moved to another table, join tables. `ModeEquivalenceTests` checks that the default transactional mode reports exactly what `RebuildDatabaseForEachMigration` reports for each of them, and that a context with `EnableRetryOnFailure` reports the same as one without.

When you find a schema MigrationTester gets wrong, add it there as a test of the behavior you want and mark it `Explicit` until it is fixed; the default run skips explicit tests. Run them with:

```bash
dotnet test tests/MigrationTester.Tests -- xUnit.Explicit=only
```

The test project holds several `DbContext` types, so pass `--context TestDbContext` to `dotnet ef`. `dotnet-tools.json` pins dotnet-ef 10.0.13 (`dotnet tool restore`).

## Public API

`src/MigrationTester/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` list every public member. Adding, removing or changing one fails the build until the lists match, so API changes show up in review. Run this to update `PublicAPI.Unshipped.txt` after an intended change:

```bash
dotnet format analyzers src/MigrationTester/MigrationTester.csproj --diagnostics RS0016 --severity info
```

## CI

`.github/workflows/ci.yml` runs on pushes to `main` and on pull requests. It builds, runs every test against SQL Server and PostgreSQL containers, packs the library, builds the examples against the packed nupkg, and uploads the nupkg as a run artifact.

## Releasing

Releases are published from GitHub Actions with [nuget.org trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored anywhere.

### One-time setup

1. Create an account on [nuget.org](https://www.nuget.org) (sign in with a Microsoft account). Your username is the name on your profile, not your email.
2. On nuget.org, open your username menu → **Trusted Publishing** → **Create**, and enter:
   - Policy name: anything, e.g. `MigrationTester releases`
   - Package owner: your account
   - Repository owner: `oklar`
   - Repository: `migrationtester`
   - Workflow file: `release.yml`
   - Environment: leave empty
3. On GitHub, open the repository → **Settings** → **Secrets and variables** → **Actions** → **Variables** → **New repository variable**: name `NUGET_USER`, value your nuget.org username.

### Each release

1. Update `CHANGELOG.md`: move the entries under `[Unreleased]` into a new `## [x.y.z] - YYYY-MM-DD` section below it, leave `[Unreleased]` empty, and add the version's link at the bottom of the file.
2. Merge to `main` and wait for CI to pass.
3. Tag the commit and push the tag:

   ```bash
   git tag v0.1.0-preview.1
   git push origin v0.1.0-preview.1
   ```

`.github/workflows/release.yml` then builds and tests the tagged commit, packs it with the tag's version (`v` dropped), pushes the `.nupkg` and `.snupkg` to nuget.org, and creates a GitHub release with both attached. Tags with a hyphen (`-preview.1`) become pre-releases on GitHub; NuGet treats them as pre-release versions. A new package can take a few minutes to appear on nuget.org while it is validated and indexed.

### Stable releases

- Before tagging, move the entries of `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`, so Shipped holds exactly what the release ships. New API added after that goes to Unshipped again.

### Versions and compatibility

Versions follow [semantic versioning](https://semver.org): a new major version (`2.0.0`) may break the API, a minor version (`2.1.0`) adds to it, a patch (`2.1.3`) fixes bugs. CI holds every package to that: before packing, `.github/scripts/package-validation-baseline.sh` finds the newest stable release on nuget.org with the same major version, and `dotnet pack` fails (error `CP0002` and friends) when the package removes or changes anything that release has. The first release of a new major version has nothing to compare with, so it may break. Nothing needs updating after a release.

To make a breaking change, raise the major version (`VersionPrefix` in `Directory.Build.props`, and the tag). To run the same check locally:

```bash
dotnet pack src/MigrationTester -c Release -p:PackageValidationBaselineVersion=$(bash .github/scripts/package-validation-baseline.sh 0.2.0)
```
