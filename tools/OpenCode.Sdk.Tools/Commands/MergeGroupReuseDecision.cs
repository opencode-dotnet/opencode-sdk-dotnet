using System.Globalization;
using System.Text.Json;

namespace OpenCode.Sdk.Tools.Commands;

internal static class MergeGroupReuseDecision
{
    public static string Reason(IReadOnlyDictionary<string, string> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        try
        {
            var queue = facts["queue-ref"].Split('/');
            if (facts["parent-count"].Trim() != "2" || queue.Length != 5 ||
                string.Join('/', queue[..4]) != "refs/heads/gh-readonly-queue/master" ||
                !long.TryParse(queue[4].Split('-').ElementAtOrDefault(1), CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                !IsHash(facts["head"]) || !IsHash(facts["base"]) || !queue[4].StartsWith("pr-", StringComparison.Ordinal))
            {
                return "rule 1: invalid group";
            }

            using var pr = JsonDocument.Parse(facts["pr.json"]);
            if (pr.RootElement.GetProperty("number").GetInt64() != number ||
                Text(pr.RootElement.GetProperty("head"), "sha") != facts["head"].Trim() ||
                Text(pr.RootElement.GetProperty("base"), "ref") != "master")
            {
                return "rule 2: PR changed";
            }

            using var runs = JsonDocument.Parse(facts["runs.json"]);
            var latest = runs.RootElement.GetProperty("workflow_runs").EnumerateArray()
                .Where(run => Text(run, "path") == ".github/workflows/ci.yml" &&
                    Text(run, "event") == "pull_request" && Text(run, "head_sha") == facts["head"].Trim())
                .OrderByDescending(run => Timestamp(run, "created_at"))
                .ThenByDescending(run => run.GetProperty("id").GetInt64()).FirstOrDefault();
            if (latest.ValueKind == JsonValueKind.Undefined || Text(latest, "status") != "completed" ||
                Text(latest, "conclusion") != "success" || latest.GetProperty("run_attempt").GetInt32() < 1)
            {
                return "rule 3: no green latest CI result";
            }

            using var jobs = JsonDocument.Parse(facts["jobs.json"]);
            var attemptJobs = jobs.RootElement.GetProperty("jobs").EnumerateArray()
                .Where(job => job.GetProperty("run_id").GetInt64() == latest.GetProperty("id").GetInt64() &&
                    job.GetProperty("run_attempt").GetInt32() == latest.GetProperty("run_attempt").GetInt32()).ToArray();
            var results = attemptJobs.Where(job => Text(job, "name") == "CI result").ToArray();
            if (results.Length != 1 || Text(results[0], "conclusion") != "success")
            {
                return "rule 3: no green latest CI result";
            }

            if (!ProvenanceHolds(facts, latest, attemptJobs))
            {
                return "rule 4: unproven tree";
            }
            return facts["changed-paths"].Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(IsGuarded)
                ? "rule 5: guarded path" : "all rules hold";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            return "malformed facts";
        }
    }

    private static bool ProvenanceHolds(IReadOnlyDictionary<string, string> facts, JsonElement run, IReadOnlyList<JsonElement> jobs)
    {
        using var artifacts = JsonDocument.Parse(facts["artifacts.json"]);
        var name = "tested-tree-" + run.GetProperty("run_attempt").GetInt32().ToString(CultureInfo.InvariantCulture);
        var matches = artifacts.RootElement.GetProperty("artifacts").EnumerateArray()
            .Where(artifact => Text(artifact, "name") == name).ToArray();
        var changes = jobs.Where(job => Text(job, "name") == "Changes").ToArray();
        if (matches.Length != 1 || matches[0].GetProperty("expired").GetBoolean() || changes.Length != 1 ||
            Text(changes[0], "conclusion") != "success")
        {
            return false;
        }
        var created = Timestamp(matches[0], "created_at");
        var testedTrees = facts["tested-tree"].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return created >= Timestamp(changes[0], "started_at") && created <= Timestamp(changes[0], "completed_at") &&
            testedTrees.Length == 2 && IsHash(facts["tree"]) && facts["tree"].Trim() == testedTrees[0] &&
            IsHash(facts["base-tree"]) && facts["base-tree"].Trim() == testedTrees[1];
    }

    private static bool IsGuarded(string path) =>
        path.StartsWith(".github/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) ||
        path.Split('/')[^1].ToUpperInvariant() is "DIRECTORY.BUILD.PROPS" or "DIRECTORY.BUILD.TARGETS" or "DIRECTORY.PACKAGES.PROPS" or
            "DIRECTORY.BUILD.RSP" or "GLOBAL.JSON" or "NUGET.CONFIG";

    private static bool IsHash(string value) => value.Trim().Length == 40 && value.Trim().All(char.IsAsciiHexDigit);
    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();
    private static DateTimeOffset Timestamp(JsonElement element, string name) =>
        DateTimeOffset.Parse(element.GetProperty(name).GetString() ?? string.Empty, CultureInfo.InvariantCulture);
}
