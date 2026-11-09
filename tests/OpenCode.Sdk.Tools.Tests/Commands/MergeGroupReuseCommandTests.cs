using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Commands;

public sealed class MergeGroupReuseCommandTests
{
    [Test]
    [Arguments("all", "all rules hold")]
    [Arguments("one-parent", "rule 1: invalid group")]
    [Arguments("moved-head", "rule 2: PR changed")]
    [Arguments("feature-base", "rule 2: PR changed")]
    [Arguments("no-run", "rule 3: no green latest CI result")]
    [Arguments("failed-attempt", "rule 3: no green latest CI result")]
    [Arguments("running-attempt", "rule 3: no green latest CI result")]
    [Arguments("newer-failed-run", "rule 3: no green latest CI result")]
    [Arguments("other-workflow", "rule 3: no green latest CI result")]
    [Arguments("failed-result", "rule 3: no green latest CI result")]
    [Arguments("old-result", "rule 3: no green latest CI result")]
    [Arguments("no-artifact", "rule 4: unproven tree")]
    [Arguments("old-artifact", "rule 4: unproven tree")]
    [Arguments("different-tree", "rule 4: unproven tree")]
    [Arguments("different-base-tree", "rule 4: unproven tree")]
    [Arguments("missing-base-tree", "rule 4: unproven tree")]
    [Arguments("replacement", "rule 4: unproven tree")]
    [Arguments("duplicate-artifact", "rule 4: unproven tree")]
    [Arguments("expired-artifact", "rule 4: unproven tree")]
    [Arguments("failed-changes", "rule 4: unproven tree")]
    [Arguments(".github/workflows/ci.yml", "rule 5: guarded path")]
    [Arguments("tools/example.cs", "rule 5: guarded path")]
    [Arguments("tools/Directory.Build.props", "rule 5: guarded path")]
    [Arguments("global.json", "rule 5: guarded path")]
    [Arguments("nested/Directory.Build.targets", "rule 5: guarded path")]
    [Arguments("nested/Directory.Packages.props", "rule 5: guarded path")]
    [Arguments("nested/Directory.Build.rsp", "rule 5: guarded path")]
    [Arguments("nested/nuget.config", "rule 5: guarded path")]
    [Arguments("nested/NuGet.Config", "rule 5: guarded path")]
    [Arguments("nested/Global.json", "rule 5: guarded path")]
    [Arguments("nested/directory.build.props", "rule 5: guarded path")]
    [Arguments(".GITHUB/workflows/ci.yml", "rule 5: guarded path")]
    [Arguments("TOOLS/example.cs", "rule 5: guarded path")]
    [Arguments("malformed", "malformed facts")]
    [Arguments("missing", "unreadable facts")]
    public async Task Execute_Should_Write_The_Decision_And_Reason(string row, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        var facts = Facts();
        ApplyRow(facts, row);
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory("facts");
        foreach (var (name, content) in facts)
        {
            await fileSystem.File.WriteAllTextAsync(fileSystem.Path.Combine("facts", name), content, cancellationToken);
        }

        var exitCode = await ToolApp.RunAsync(["merge-group-reuse", "--facts", "facts", "--output", "output"],
            cancellationToken, services =>
        {
            services.AddSingleton<IFileSystem>(fileSystem);
            services.AddSingleton<IAnsiConsole, TestConsole>();
        });

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(await fileSystem.File.ReadAllTextAsync("output", cancellationToken)).IsEqualTo(
            $"reuse={(row == "all" ? "true" : "false")}\nreason={reason}\n");
    }

    private static void ApplyRow(Dictionary<string, string> facts, string row)
    {
        // These documents are built below with non-null arrays and members.
        var runs = JsonNode.Parse(facts["runs.json"])!["workflow_runs"]!.AsArray();
        var jobs = JsonNode.Parse(facts["jobs.json"])!["jobs"]!.AsArray();
        var artifacts = JsonNode.Parse(facts["artifacts.json"])!["artifacts"]!.AsArray();
        ApplyJsonRow(row, runs, jobs, artifacts);
        facts["runs.json"] = new JsonObject { ["workflow_runs"] = runs.DeepClone() }.ToJsonString();
        facts["jobs.json"] = new JsonObject { ["jobs"] = jobs.DeepClone() }.ToJsonString();
        facts["artifacts.json"] = new JsonObject { ["artifacts"] = artifacts.DeepClone() }.ToJsonString();
        if (row.Contains('/', StringComparison.Ordinal) || row == "global.json")
        {
            facts["changed-paths"] = row + '\0';
        }
        switch (row)
        {
            case "one-parent":
                facts["parent-count"] = "1";
                break;
            case "moved-head":
                facts["head"] = new string('d', 40);
                break;
            case "feature-base":
                facts["pr.json"] = facts["pr.json"].Replace("master", "feature/example", StringComparison.Ordinal);
                break;
            case "different-tree":
                facts["tested-tree"] = new string('d', 40) + '\n' + new string('b', 40);
                break;
            case "different-base-tree":
                facts["base-tree"] = new string('d', 40);
                break;
            case "missing-base-tree":
                facts["tested-tree"] = new string('c', 40);
                break;
            case "malformed":
                facts["pr.json"] = "{";
                break;
            case "missing":
                facts.Remove("head");
                break;
        }
    }

    private static void ApplyJsonRow(string row, JsonArray runs, JsonArray jobs, JsonArray artifacts)
    {
        switch (row)
        {
            case "no-run":
                runs.Clear();
                break;
            case "failed-attempt":
                runs[1]!["conclusion"] = "failure";
                break;
            case "running-attempt":
                runs[1]!["status"] = "in_progress";
                runs[1]!["conclusion"] = null;
                break;
            case "newer-failed-run":
                runs.Add(JsonSerializer.SerializeToNode(Run(30, 1)));
                runs[2]!["created_at"] = "2026-11-09T11:00:00Z";
                runs[2]!["conclusion"] = "failure";
                break;
            case "other-workflow":
                foreach (var run in runs)
                {
                    run!["path"] = ".github/workflows/other.yml";
                }
                break;
            case "failed-result":
                jobs[0]!["conclusion"] = "failure";
                break;
            case "old-result":
                jobs[0]!["run_attempt"] = 1;
                break;
            case "no-artifact":
                artifacts.Clear();
                break;
            case "old-artifact":
                artifacts[0]!["name"] = "tested-tree-1";
                break;
            case "replacement":
                artifacts[0]!["created_at"] = "2026-11-09T10:03:00Z";
                break;
            case "duplicate-artifact":
                artifacts.Add(artifacts[0]!.DeepClone());
                break;
            case "expired-artifact":
                artifacts[0]!["expired"] = true;
                break;
            case "failed-changes":
                jobs[1]!["conclusion"] = "failure";
                break;
        }

    }

    private static Dictionary<string, string> Facts() => new(StringComparer.Ordinal)
    {
        ["queue-ref"] = "refs/heads/gh-readonly-queue/master/pr-806-base",
        ["parent-count"] = "2",
        ["head"] = new string('a', 40),
        ["base"] = new string('b', 40),
        ["base-tree"] = new string('b', 40),
        ["tree"] = new string('c', 40),
        ["tested-tree"] = new string('c', 40) + '\n' + new string('b', 40),
        ["changed-paths"] = "src/example.cs\0",
        ["pr.json"] = JsonSerializer.Serialize(new { number = 806, head = new { sha = new string('a', 40) }, @base = new { @ref = "master" } }),
        ["runs.json"] = JsonSerializer.Serialize(new { workflow_runs = new[] { Run(10, 1), Run(20, 2) } }),
        ["jobs.json"] = JsonSerializer.Serialize(new { jobs = new[] { Job("CI result"), Job("Changes") } }),
        ["artifacts.json"] = JsonSerializer.Serialize(new { artifacts = new[] { new { id = 30, name = "tested-tree-2", expired = false, created_at = "2026-11-09T10:01:00Z" } } }),
    };

    private static object Run(int id, int attempt) => new
    {
        id,
        run_attempt = attempt,
        path = ".github/workflows/ci.yml",
        @event = "pull_request",
        head_sha = new string('a', 40),
        status = "completed",
        conclusion = "success",
        created_at = $"2026-11-09T{(id == 10 ? "09" : "10")}:00:00Z",
    };

    private static object Job(string name) => new
    {
        name,
        run_id = 20,
        run_attempt = 2,
        conclusion = "success",
        started_at = "2026-11-09T10:00:00Z",
        completed_at = "2026-11-09T10:02:00Z",
    };
}
