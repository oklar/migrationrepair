# MigrationTester.Examples

A consumer sample: an EF Core model with ten SQL Server migrations, and tests that run MigrationTester over them. It builds against the library source by default, so there is nothing to pack first.

## Prerequisites

- .NET 10 SDK
- SQL Server (local, Docker, or remote) and a login that can create and drop databases

## Configuration

| Env var | Purpose |
|--------|---------|
| `MIGRATION_TESTER_SCENARIO_TEST` | Connection string for the tests. Its database name only names the throwaway databases (`MigrationTesterExamples_Test_mt_…`); each run creates and drops its own. |
| `MIGRATION_TESTER_SCENARIO_DESIGN` | Connection string for `dotnet ef` design time. |

Defaults use **Windows auth** on the default local instance, with databases `MigrationTesterExamples_Test` / `MigrationTesterExamples_Design`.

## Migrations

| # | Migration | What MigrationTester plants |
|---|-----------|---------------------------|
| 01 | `InitialSchema` | Nothing; the first migration has no earlier data, so it passes |
| 02 | `AddForeignKeyOrdersCustomers` | An `Orders` row whose `CustomerId` matches no customer |
| 03 | `ShrinkProductCodeLength` | A `Products.Code` longer than the new max length |
| 04 | `AddUniqueProductCode` | Two products with the same `Code` |
| 05 | `MakeProductNameRequired` | A product with a NULL `Name` |
| 06 | `AddOrderStatus` | Existing `Orders` rows, which a NOT NULL column without a default cannot fill |
| 07 | `ChangeTagExternalIdToGuid` | A `Tags.ExternalId` that is not a GUID |
| 08 | `ShrinkProductPricePrecision` | A `Products.Price` too large for `decimal(10,2)` |
| 09 | `AddCheckConstraintPositivePrice` | A `Products.Price` of 0, which the new `[Price] > 0` constraint rejects |
| 10 | `ChangeCustomerNameToVarchar` | A `Customers.Name` holding an emoji, which `varchar` keeps only as `??`: SQL Server converts it without an error, so the migration would lose the character silently |

## Commands

From the repository root:

```bash
dotnet test examples/MigrationTester.Examples
```

`Each_broken_migration_fails_on_its_kind_of_data` asserts that 02–10 fail, each matched to its kind of data, and that 01 passes, so it is green.

`Migration_handles_data_that_exists_before_it` is the pattern you would use in your own project: one `[Theory]` case per migration, each calling `ThrowIfFailed()`. Here 02–10 fail by design and show the full report with T-SQL repair hints, so it is `Explicit`; run it by asking for it:

```bash
dotnet test examples/MigrationTester.Examples --logger "console;verbosity=detailed" -- xUnit.Explicit=only
```

EF tools (`dotnet tool restore` once, from the repository root):

```bash
dotnet ef migrations list --project examples/MigrationTester.Examples
```

## Against the packed package

To build the sample the way a NuGet consumer would:

```bash
dotnet pack src/MigrationTester -c Release
dotnet build examples/MigrationTester.Examples -p:UseMigrationTesterPackage=true
```

Package mode restores from `artifacts/packages` into `artifacts/examples-packages` instead of the global NuGet cache, so a locally packed version never shadows a published one. After repacking the same version, delete `artifacts/examples-packages` to pick up the new build. CI runs this on every push.
