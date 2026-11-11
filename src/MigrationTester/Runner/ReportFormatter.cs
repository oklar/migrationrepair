using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace MigrationTester.Runner;

/// <summary>Readable text for <see cref="MigrationStepResult.Summary"/> and <see cref="MigrationTestReport.ToString"/>.</summary>
internal static class ReportFormatter
{
    public static string Report(MigrationTestReport report)
    {
        if (report.Steps.Count == 0)
        {
            return "MigrationTester: no migrations to test.";
        }

        var sb = new StringBuilder();
        int failed = report.Steps.Count(static s => !s.Succeeded);
        sb.Append(failed == 0
            ? $"MigrationTester: all {Plural(report.Steps.Count, "migration")} passed"
            : $"MigrationTester: {failed} of {Plural(report.Steps.Count, "migration")} failed");
        sb.AppendLine(report.DatabaseKept ? $" (database '{report.DatabaseName}' kept)." : ".");
        foreach (string warning in report.Warnings)
        {
            sb.AppendLine($"Warning: {warning}");
        }

        foreach (var step in report.Steps.Where(static s => !s.Succeeded))
        {
            sb.AppendLine();
            sb.AppendLine(Indent(Step(step)));
        }

        if (failed < report.Steps.Count)
        {
            sb.AppendLine();
            foreach (var step in report.Steps.Where(static s => s.Succeeded))
            {
                sb.AppendLine($"passed {step.MigrationId} ({Planted(step)}, {Seconds(step.Duration)})");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string Step(MigrationStepResult step)
    {
        var sb = new StringBuilder();
        switch (step.Outcome)
        {
            case MigrationStepOutcome.Error:
                sb.AppendLine($"ERROR {step.MigrationId}: could not test it. {step.ErrorStage} failed:");
                sb.AppendLine(ErrorText(step.Exception));
                break;

            case MigrationStepOutcome.Failed when step.Exception is null:
                sb.AppendLine($"FAILED {step.MigrationId}: Up() changed data that is valid before it runs.");
                AppendFailingScenarios(sb, step);
                break;

            case MigrationStepOutcome.Failed:
                sb.AppendLine($"FAILED {step.MigrationId}: Up() failed on data that is valid before it runs.");
                sb.AppendLine(ErrorText(step.Exception));
                AppendFailingScenarios(sb, step);
                break;

            default:
                sb.AppendLine(step.Succeeded
                    ? $"passed {step.MigrationId} ({Planted(step)})."
                    : $"FAILED {step.MigrationId}: the migration passed, but not every scenario could be seeded (RequireAllScenariosSeeded).");
                break;
        }

        var notSeeded = step.Scenarios.Where(static s => s.Status == ScenarioStatus.NotSeeded).ToList();
        if (notSeeded.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not seeded (not tested):");
            foreach (var s in notSeeded)
            {
                sb.AppendLine($" - [{s.Kind}] {s.Name}: {s.Reason}");
            }
        }

        if (step.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (string warning in step.Warnings)
            {
                sb.AppendLine($" - {warning}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendFailingScenarios(StringBuilder sb, MigrationStepResult step)
    {
        var planted = step.Scenarios.Where(static s => s.Status == ScenarioStatus.Planted).ToList();
        sb.AppendLine();
        if (step.FailingScenarios.Count > 0)
        {
            sb.AppendLine("Broke on:");
            foreach (var s in step.FailingScenarios)
            {
                sb.AppendLine(s.Change is { } change
                    ? $" - [{s.Kind}] {s.Name}: {change.Before} became {change.After ?? "NULL"}"
                    : $" - [{s.Kind}] {s.Name}");
            }

            sb.AppendLine();
            sb.AppendLine("Repair hint:");
            foreach (var s in step.FailingScenarios)
            {
                sb.AppendLine($"-- {s.Name}");
                sb.AppendLine(s.RepairHint);
            }
        }
        else if (planted.Count > 0)
        {
            sb.AppendLine("The error matches none of the planted scenarios, so the migration may fail even without them. Planted:");
            foreach (var s in planted)
            {
                sb.AppendLine($" - [{s.Kind}] {s.Name}");
            }
        }
        else
        {
            sb.AppendLine("No scenarios were planted, so the migration fails even on ordinary data.");
        }
    }

    private static string ErrorText(Exception? exception)
    {
        if (exception is null)
        {
            return "";
        }

        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql)
            {
                return $"SQL Server error {sql.Number}: {sql.Message}";
            }
        }

        return $"{exception.GetType().Name}: {exception.Message}";
    }

    private static string Planted(MigrationStepResult step)
        => Plural(step.Scenarios.Count(static s => s.Status == ScenarioStatus.Planted), "scenario") + " planted";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Seconds(TimeSpan duration) => duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static string Indent(string text) => "  " + text.ReplaceLineEndings(Environment.NewLine + "  ");
}
