using OpenCode.Sdk.Tools.Tests.Packaging.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

public sealed class CompatibilitySuppressionClassifierTests
{
    [Test]
    [Arguments("1.0.1", "1.0.0", true, null)]
    [Arguments("1.0.1", "1.0.0", false, "OpenCode.Sdk: CP0001 'T:OpenCode.Sdk.Models.AgentInfo': a generated-type break needs a release that moves the protocol pin.")]
    [Arguments("1.1.0", "1.0.0", false, "OpenCode.Sdk: CP0001 'T:OpenCode.Sdk.Models.AgentInfo': a generated-type break needs a release that moves the protocol pin.")]
    [Arguments("1.0.0", "1.0.0-rc.1", false, "OpenCode.Sdk: CP0001 'T:OpenCode.Sdk.Models.AgentInfo': a generated-type break needs a release that moves the protocol pin.")]
    [Arguments("1.0.0-rc.2", "1.0.0-rc.1", false, "OpenCode.Sdk: CP0001 'T:OpenCode.Sdk.Models.AgentInfo': a generated-type break needs a release that moves the protocol pin.")]
    [Arguments("2.0.0", "1.0.0", false, null)]
    public async Task Validate_Should_Accept_A_Generated_Break_Only_Where_The_Step_And_The_Pin_Permit(string version, string baseline, bool pinMoved, string? expectedViolation)
    {
        var scenario = new PackageValidationScenario()
            .WithVersions(version, baseline)
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithSuppression("T:OpenCode.Sdk.Models.AgentInfo");
        if (pinMoved)
        {
            scenario.WithMovedPin();
        }

        var evidence = await scenario.ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        if (expectedViolation is null)
        {
            await Assert.That(violations).IsEmpty();
        }
        else
        {
            await Assert.That(violations).IsEquivalentTo([expectedViolation], CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments("1.0.1", "1.0.0")]
    [Arguments("1.1.0", "1.0.0")]
    public async Task Validate_Should_Require_A_Major_For_A_Hand_Written_Break_With_A_Moved_Pin(string version, string baseline)
    {
        var evidence = await new PackageValidationScenario()
            .WithVersions(version, baseline)
            .WithMovedPin()
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithSuppression("M:OpenCode.Sdk.OpenCodeClient.Dispose")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEquivalentTo(
            ["OpenCode.Sdk: CP0001 'M:OpenCode.Sdk.OpenCodeClient.Dispose': this hand-written break needs a major version step."],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task Validate_Should_Allow_A_Generated_Member_In_A_Minor_Step()
    {
        var evidence = await new PackageValidationScenario()
            .WithVersions("1.1.0", "1.0.0")
            .WithMovedPin()
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithSuppression("M:OpenCode.Sdk.Models.AgentInfo.get_Id")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Validate_Should_Require_A_Major_Step_For_A_Hand_Written_Member()
    {
        var evidence = await new PackageValidationScenario()
            .WithVersions("1.1.0", "1.0.0")
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithSuppression("M:OpenCode.Sdk.OpenCodeClient.Dispose")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations.Single()).Contains("OpenCode.Sdk.OpenCodeClient").And.Contains("major");
    }

    [Test]
    public async Task Validate_Should_Allow_A_Removed_Generated_Type_From_The_Baseline()
    {
        var evidence = await new PackageValidationScenario()
            .WithVersions("1.1.0", "1.0.0")
            .WithMovedPin()
            .WithRemovedGeneratedType("Models/OldAgentInfo.cs", "OldAgentInfo")
            .WithSuppression("T:OpenCode.Sdk.Models.OldAgentInfo")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Validate_Should_Allow_A_Nested_Generated_Member_With_Parameters()
    {
        var evidence = await new PackageValidationScenario()
            .WithVersions("1.1.0", "1.0.0")
            .WithMovedPin()
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithSuppression("M:OpenCode.Sdk.Models.AgentInfo.Nested.Update(System.String)")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Validate_Should_Allow_A_Hand_Written_Break_In_A_Major_Step()
    {
        var evidence = await new PackageValidationScenario()
            .WithVersions("2.0.0", "1.9.0")
            .WithSuppression("T:OpenCode.Sdk.OpenCodeClient")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments("0.9.0", "", false)]
    [Arguments("1.0.1", "1.0.0", false)]
    [Arguments("1.1.0", "1.0.0", false)]
    [Arguments("2.0.0", "1.0.0", false)]
    [Arguments("1.0.1", "1.0.0", true)]
    public async Task Validate_Should_Refuse_A_Cross_Asset_Suppression_In_Every_Step(string version, string baseline, bool moved)
    {
        var scenario = new PackageValidationScenario()
            .WithVersions(version, baseline)
            .WithSuppression("T:OpenCode.Sdk.Models.AgentInfo", isBaseline: false);
        if (moved)
        {
            scenario.WithMovedPin();
        }

        var evidence = await scenario.ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEquivalentTo(
            ["OpenCode.Sdk: CP0001 'T:OpenCode.Sdk.Models.AgentInfo': cross-asset suppressions are forbidden; every asset must expose the same API."],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task Validate_Should_Refuse_Any_Suppression_Without_A_Baseline()
    {
        var evidence = await new PackageValidationScenario()
            .WithSuppression("T:OpenCode.Sdk.Models.AgentInfo")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations.Single()).Contains("no baseline");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Validate_Should_Require_A_Major_Step_For_Extensions(bool moved)
    {
        var scenario = new PackageValidationScenario()
            .WithVersions("1.1.0", "1.0.0")
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithSuppression("T:OpenCode.Sdk.Models.AgentInfo", project: "OpenCode.Sdk.Extensions");
        if (moved)
        {
            scenario.WithMovedPin();
        }

        var evidence = await scenario.ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEquivalentTo(
            ["OpenCode.Sdk.Extensions: CP0001 'T:OpenCode.Sdk.Models.AgentInfo': this hand-written break needs a major version step."],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task Validate_Should_Refuse_A_Hand_Written_Partial_Of_A_Generated_Type()
    {
        var evidence = await new PackageValidationScenario()
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithHandWrittenPartial("Models/AgentInfo.Custom.cs", "AgentInfo")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations.Single()).Contains("AgentInfo.Custom.cs").And.Contains("partial");
    }

    [Test]
    public async Task Validate_Should_Not_Confuse_A_Type_Name_Prefix_With_A_Generated_Type()
    {
        var evidence = await new PackageValidationScenario()
            .WithVersions("1.1.0", "1.0.0")
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithSuppression("P:OpenCode.Sdk.Models.AgentInfoCustom.Id")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations.Single()).Contains("AgentInfoCustom").And.Contains("major");
    }

    [Test]
    [Arguments("obj/AgentInfo.Custom.cs")]
    [Arguments("bin/AgentInfo.Custom.cs")]
    public async Task Validate_Should_Ignore_Intermediate_And_Output_Partials(string path)
    {
        var evidence = await new PackageValidationScenario()
            .WithGeneratedType("Models/AgentInfo.cs", "AgentInfo")
            .WithHandWrittenPartial(path, "AgentInfo")
            .ReadAsync();

        var violations = new CompatibilitySuppressionClassifier(evidence).Validate();

        await Assert.That(violations).IsEmpty();
    }
}
