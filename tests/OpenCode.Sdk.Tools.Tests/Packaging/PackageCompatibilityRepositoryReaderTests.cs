using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Tests.Packaging.Support;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

public sealed class PackageCompatibilityRepositoryReaderTests
{
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
