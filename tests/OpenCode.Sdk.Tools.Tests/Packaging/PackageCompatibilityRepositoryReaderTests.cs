using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Packaging.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

public sealed class PackageCompatibilityRepositoryReaderTests
{
    [Test]
    [Arguments(false, "spec/receipt.json")]
    [Arguments(true, "v1.0.0:spec/receipt.json")]
    public async Task ReadAsync_Should_Name_An_Invalid_Receipt(bool baseline, string receipt)
    {
        var scenario = new PackageValidationScenario()
            .WithVersions("1.0.1", "1.0.0")
            .WithInvalidReceipt(baseline);

        var exception = await Assert.That(async () => _ = await scenario.ReadAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).IsEqualTo($"'{receipt}' must contain a string 'upstreamCommit'.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReadAsync_Should_Compare_The_Current_Pin_With_The_Baseline_Pin(bool moved)
    {
        var scenario = new PackageValidationScenario().WithVersions("1.0.1", "1.0.0");
        if (moved)
        {
            scenario.WithMovedPin();
        }

        var evidence = await scenario.ReadAsync();

        await Assert.That(evidence.PinMoved).IsEqualTo(moved);
    }

    [Test]
    public async Task ReadAsync_Should_Tell_The_Caller_To_Fetch_A_Baseline_Without_A_Receipt()
    {
        var scenario = new PackageValidationScenario().WithVersions("1.0.1", "1.0.0").WithMissingBaselineReceipt();

        var exception = await Assert.That(async () => _ = await scenario.ReadAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).IsEqualTo(
            "Cannot read 'spec/receipt.json' at baseline tag 'v1.0.0'. Fetch tags with 'git fetch --tags' before running this check. fatal: path does not exist");
    }

    [Test]
    public async Task ReadAsync_Should_Keep_Repository_Suppressions_Within_The_Version_Step()
    {
        var fileSystem = new RealFileSystem();
        var repository = new RepositoryRoot(fileSystem).Locate();
        var evidence = await new PackageCompatibilityRepositoryReader(fileSystem, new CliWrapProcessRunner())
            .ReadAsync(repository, CancellationToken.None);

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task ReadAsync_Should_Need_No_Git_Without_A_Baseline()
    {
        var scenario = new PackageValidationScenario().WithGeneratedType("Models/AgentInfo.cs", "AgentInfo");

        var evidence = await scenario.ReadAsync();

        await Assert.That(evidence.GeneratedTypes).Contains("OpenCode.Sdk.Models.AgentInfo");
        await Assert.That(scenario.Runner.Invocations).IsEmpty();
    }

    [Test]
    public async Task ReadAsync_Should_Tell_The_Caller_To_Fetch_A_Missing_Baseline_Tag()
    {
        var scenario = new PackageValidationScenario().WithVersions("1.1.0", "1.0.0").WithMissingBaselineTag();

        var exception = await Assert.That(async () => _ = await scenario.ReadAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("v1.0.0").And.Contains("Fetch tags");
    }

    [Test]
    [Arguments("DiagnosticId")]
    [Arguments("Target")]
    [Arguments("Left")]
    [Arguments("Right")]
    [Arguments("IsBaselineSuppression")]
    public async Task ReadAsync_Should_Refuse_A_Suppression_With_A_Missing_Field(string field)
    {
        var scenario = new PackageValidationScenario()
            .WithSuppression("T:OpenCode.Sdk.Models.AgentInfo")
            .WithoutSuppressionField(field);

        var exception = await Assert.That(async () => _ = await scenario.ReadAsync()).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(field);
    }
}
