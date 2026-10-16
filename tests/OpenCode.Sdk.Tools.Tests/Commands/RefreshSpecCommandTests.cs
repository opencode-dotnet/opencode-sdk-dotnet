using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Generator.Refresh.Support;

namespace OpenCode.Sdk.Tools.Tests.Commands;

public sealed class RefreshSpecCommandTests
{
    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, true, false)]
    [Arguments(true, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    public async Task Execute_Should_Refuse_Anything_But_One_Mode(bool prepare, bool verify, bool apply)
    {
        using var scenario = new RefreshScenario();
        var arguments = new List<string> { "refresh-spec" };
        if (prepare)
        {
            arguments.AddRange(["--ref", "v2.0.30"]);
        }
        if (verify)
        {
            arguments.Add("--verify");
        }
        if (apply)
        {
            arguments.AddRange(["--apply", RefreshScenario.ReceiptPath]);
        }

        var result = await scenario.RunCommandAsync([.. arguments]);

        await Assert.That(result.ExitCode).IsNotEqualTo(0);
        await Assert.That(result.Output).Contains("Specify exactly one of --ref <REF>, --verify, or --apply <RECEIPT>.");
        await Assert.That(scenario.Runner.Invocations).IsEmpty();
    }

    [Test]
    [Arguments("prepare", 0, "Receipt prepared:")]
    [Arguments("apply", 0, "Accepted snapshot applied:")]
    [Arguments("verify", 0, "The accepted snapshot reproduces its receipt")]
    [Arguments("unreproduced", 1, "no committed receipt exists; the accepted snapshot predates the synchronizer")]
    [Arguments("refusal", 1, "Refused: receipt '[red].json' does not exist")]
    public async Task Execute_Should_Dispatch_One_Mode_And_Map_Its_Outcome(string mode, int exitCode, string diagnostic)
    {
        using var scenario = await RefreshScenario.CreateAsync();
        if (mode == "prepare")
        {
            _ = scenario.ExpectReference(RefreshScenario.Document, "v2.0.30", "refs/tags/v2.0.30");
            scenario.ExpectUnpatchedGeneration();
        }
        else if (mode == "apply")
        {
            _ = scenario.ExpectApply();
        }
        else if (mode != "refusal")
        {
            scenario.ExpectHead();
        }
        if (mode == "unreproduced")
        {
            scenario.FileSystem.File.Delete(SnapshotPaths.CommittedReceipt);
        }
        var arguments = mode switch
        {
            "prepare" => new[] { "--ref", "v2.0.30" },
            "apply" => ["--apply", RefreshScenario.ReceiptPath],
            "refusal" => ["--apply", "[red].json"],
            _ => ["--verify"],
        };

        var result = await scenario.RunCommandAsync(["refresh-spec", .. arguments]);

        await Assert.That(result.ExitCode).IsEqualTo(exitCode);
        await Assert.That(result.Output).Contains(diagnostic);
    }
}
