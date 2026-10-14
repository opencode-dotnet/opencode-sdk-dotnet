using System.Globalization;
using OpenCode.Sdk.Tools.TestRoster.Models;

namespace OpenCode.Sdk.Tools.TestRoster;

/// <summary>Compares one roster leg's expected modules against the TRX summaries of its results
/// directory; every offender lands in one report instead of failing on the first. Entries are
/// expected to be unique — the loader refuses a roster that lists one twice.</summary>
internal static class TestRosterComparator
{
    public static TestRosterComparison Compare(IReadOnlyList<string> expectedModules, IReadOnlyList<TestRunSummary> runs)
    {
        var expected = new HashSet<string>(expectedModules, StringComparer.Ordinal);
        var runsByModule = new Dictionary<string, List<TestRunSummary>>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            if (runsByModule.TryGetValue(run.Module, out var moduleRuns))
            {
                moduleRuns.Add(run);
            }
            else
            {
                runsByModule[run.Module] = [run];
            }
        }

        var rows = new List<TestRosterRow>();
        var failures = new List<string>();
        foreach (var module in expectedModules)
        {
            if (!runsByModule.TryGetValue(module, out var moduleRuns))
            {
                failures.Add($"{module}: no TRX found (the module did not run)");
                continue;
            }

            if (moduleRuns.Count > 1)
            {
                failures.Add($"{module}: {Count(moduleRuns.Count)} TRX files name the same module ({FileNames(moduleRuns)})");
                continue;
            }

            var run = moduleRuns[0];
            if (run.Total == 0 || run.Executed == 0)
            {
                failures.Add(
                    $"{module}: '{run.FileName}' reports total={Count(run.Total)}, executed={Count(run.Executed)} "
                    + "(the module ran no test)");
                continue;
            }

            rows.Add(new TestRosterRow(module, run.Executed));
        }

        foreach (var unexpected in runsByModule.Where(pair => !expected.Contains(pair.Key))
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            failures.Add(
                $"{unexpected.Key}: the roster does not list this module ({FileNames(unexpected.Value)}); add it to the roster");
        }

        return new TestRosterComparison(rows.AsReadOnly(), failures.AsReadOnly());
    }

    private static string FileNames(IReadOnlyList<TestRunSummary> runs) =>
        string.Join(", ", runs.Select(static run => $"'{run.FileName}'"));

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
